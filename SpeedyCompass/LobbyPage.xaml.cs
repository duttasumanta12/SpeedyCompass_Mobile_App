#if ANDROID
using AndroidX.ConstraintLayout.Core.Motion.Utils;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SpeedyCompass.Controls;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
#if ANDROID
using static Android.Provider.Contacts.Intents;
#endif

namespace SpeedyCompass;

// Google Routes API Models
public class RoutesRequest { [JsonPropertyName("origin")] public RouteWaypoint Origin { get; set; } [JsonPropertyName("destination")] public RouteWaypoint Destination { get; set; } [JsonPropertyName("travelMode")] public string TravelMode { get; set; } = "DRIVE"; }
public class RouteWaypoint { [JsonPropertyName("location")] public RouteLocation Location { get; set; } }
public class RouteLocation { [JsonPropertyName("latLng")] public RouteLatLng LatLng { get; set; } }
public class RouteLatLng { [JsonPropertyName("latitude")] public double Latitude { get; set; } [JsonPropertyName("longitude")] public double Longitude { get; set; } }
public class RoutesResponse { [JsonPropertyName("routes")] public List<RouteData> Routes { get; set; } }
public class RouteData { [JsonPropertyName("distanceMeters")] public int DistanceMeters { get; set; } [JsonPropertyName("duration")] public string Duration { get; set; } [JsonPropertyName("polyline")] public RoutePolyline Polyline { get; set; } }
public class RoutePolyline { [JsonPropertyName("encodedPolyline")] public string EncodedPolyline { get; set; } }

public class Rider
{
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = false;
    public string RoleDisplay => IsAdmin ? "Admin" : "Rider";
    public Color RoleColor => IsAdmin ? Colors.DarkOrange : Colors.Gray;
}

// MVVM Model for the Map Pins
public class MapPinViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private Location _location;
    private string _speed;
    private string _username;
    private Color _pinColor;

    public bool IsDestination { get; set; } = false;

    public Location Location { get => _location; set { _location = value; OnPropertyChanged(); } }
    public string Speed { get => _speed; set { _speed = value; OnPropertyChanged(); } }
    public string Username { get => _username; set { _username = value; OnPropertyChanged(); } }
    public Color PinColor { get => _pinColor; set { _pinColor = value; OnPropertyChanged(); } }

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

// DataTemplateSelector determines which pin UI to draw based on the model's properties
public class MapPinTemplateSelector : DataTemplateSelector
{
    public DataTemplate RiderTemplate { get; set; }
    public DataTemplate DestinationTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is MapPinViewModel vm && vm.IsDestination)
        {
            // Use standard Google Map Pin for destinations
            return DestinationTemplate;
        }

        // Use custom speed bubble for real users
        return RiderTemplate;
    }
}

public partial class LobbyPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly string _googleApiKey;
    private static readonly HttpClient _httpClient = new();

    private bool _hasJoined = false;
    private bool _amIAdmin = false;
    private string _myName = "";

    // UI State Collections for XAML Binding
    public ObservableCollection<Rider> Riders { get; set; } = new();
    public ObservableCollection<RiderPin> MapPins { get; }
    = new ObservableCollection<RiderPin>();

    // Tracking State
    private bool _isTracking = false;
    private bool _routeIsActive = false;
    private Location _lastKnownLocation;
    private RiderPin _myPinVm;

    // Destination State
    private Location _pendingDestination;
    private Location _activeDestination;
    private Polyline _activeRouteLine;

    private readonly ConcurrentDictionary<string, RiderPin> _riderViewModels = new();
    private readonly Random _randomColorGen = new();

    // --- NEW SIMULATION VARIABLES ---
    private List<Location> _currentRoutePoints = new();
    private bool _isSimulating = false;

    private double _currentHeading = 0; // Added to track our current rotation
    private int _autocompleteApiHits = 0;
    private CancellationTokenSource _debounceCts;

    public LobbyPage(SignalRService signalRService, string groupName)
    {
        InitializeComponent();

        // Ensure UI elements bind to this code-behind class
        BindingContext = this;

        _signalRService = signalRService;

        var config = Application.Current?.MainPage?.Handler?.MauiContext?.Services?.GetService<IConfiguration>();
        _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk" ?? throw new Exception("API Key missing");

        GroupNameLabel.Text = groupName;
        _myName = Preferences.Default.Get("username", "Unknown");
        _amIAdmin = Preferences.Default.Get("IsAdmin", false);

        CurrentUserNameLabel.Text = _myName;
        CurrentUserRoleLabel.Text = _amIAdmin ? "Admin" : "Rider";

        AdminSearchUI.IsVisible = _amIAdmin;
        AdminInstructionBanner.IsVisible = _amIAdmin;

        RidersCollectionView.ItemsSource = Riders;

        // Hook up SignalR events
        _signalRService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _signalRService.RosterUpdated += OnRosterUpdated;
        _signalRService.NavigationStarted += OnNavigationStarted;
        _signalRService.RiderLocationUpdated += OnRiderLocationUpdated;
        _signalRService.NavigationCancelled += OnNavigationCancelled; // NEW
    }

    protected override async void OnAppearing()
    {

        base.OnAppearing();
        if (_hasJoined) return;

        try
        {
            if (_amIAdmin) await _signalRService.CreateGroup(GroupNameLabel.Text, _myName);
            else await _signalRService.JoinGroup(GroupNameLabel.Text, _myName);

            _hasJoined = true;

            InitializeLocalTrackingAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not join: {ex.Message}", "OK");
            await Navigation.PopAsync();
        }
    }

    private void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            StatusLabel.TextColor = color;
            StatusDot.BackgroundColor = color;
        });
    }

    private void OnTabClicked(object sender, EventArgs e)
    {
        if (sender == TabRoster)
        {
            TabRoster.BackgroundColor = Colors.DodgerBlue;
            TabRoster.TextColor = Colors.White;
            TabMap.BackgroundColor = Colors.Transparent;
            TabMap.TextColor = Application.Current.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black;

            RosterView.IsVisible = true;
            MapView.IsVisible = false;
        }
        else if (sender == TabMap)
        {
            TabMap.BackgroundColor = Colors.DodgerBlue;
            TabMap.TextColor = Colors.White;
            TabRoster.BackgroundColor = Colors.Transparent;
            TabRoster.TextColor = Application.Current.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black;

            RosterView.IsVisible = false;
            MapView.IsVisible = true;

            FitMapToBounds();
        }
    }

    // --- SEARCH AND DESTINATION LOGIC ---
    private async void OnMapClicked(object sender, MapClickedEventArgs e)
    {
        if (!_amIAdmin || _routeIsActive) return;

        _pendingDestination = e.Location;
        UpdateDestinationPin(_pendingDestination, "Selected Destination");

        try
        {
            // Reverse Geocoding: Turn Map Coordinates into an Address
            var placemarks = await Geocoding.Default.GetPlacemarksAsync(e.Location.Latitude, e.Location.Longitude);
            var placemark = placemarks?.FirstOrDefault();
            if (placemark != null)
            {
                DestinationSearchBar.Text = $"{placemark.FeatureName} {placemark.Thoroughfare}, {placemark.Locality}".Trim(' ', ',');
            }
            else
            {
                DestinationSearchBar.Text = $"{e.Location.Latitude:F4}, {e.Location.Longitude:F4}";
            }
        }
        catch { DestinationSearchBar.Text = $"{e.Location.Latitude:F4}, {e.Location.Longitude:F4}"; }

        ConfirmDestButton.IsEnabled = true;
        ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
    }

    private async void OnSearchPressed(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DestinationSearchBar.Text)) return;

        try
        {
            // Forward Geocoding: Turn Address into Map Coordinates
            var locations = await Geocoding.Default.GetLocationsAsync(DestinationSearchBar.Text);
            var location = locations?.FirstOrDefault();
            if (location != null)
            {
                _pendingDestination = location;
                UpdateDestinationPin(_pendingDestination, DestinationSearchBar.Text);
                LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(location, Distance.FromMiles(2)));

                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
            }
            else
            {
                await DisplayAlert("Not Found", "Could not find that location.", "OK");
            }
        }
        catch (Exception) { await DisplayAlert("Error", "Geocoding failed.", "OK"); }
    }

    private void UpdateDestinationPin(Location location, string label)
    {
        // Remove old destination VM if it exists
        var oldDest = LiveMap.Pins.FirstOrDefault();
        if (oldDest != null) LiveMap.Pins.Remove(oldDest);

        // Add new destination VM (TemplateSelector will detect IsDestination = true)
        LiveMap.Pins.Add(new Pin() { Label = label, Location = location });
    }

    // --- NAVIGATION CONFIRMATION ---
    private async void OnConfirmDestinationClicked(object sender, EventArgs e)
    {
        if (_pendingDestination == null || _lastKnownLocation == null) return;

        // UI State Change: Lock search, swap buttons, keep search bar visible
        ConfirmDestButton.IsVisible = false;
        ResetDestButton.IsVisible = true;
        DestinationSearchBar.IsReadOnly = true;
        AdminInstructionBanner.IsVisible = false;
        _routeIsActive = true;
        EnableNavigationUI();

        string destName = DestinationSearchBar.Text ?? "Destination";

        await CalculateAndDrawRoute(_lastKnownLocation, _pendingDestination);

        _activeDestination = _pendingDestination;
        StartNavButton.IsVisible = true;
        //FitMapToBounds();

        // Inform the entire group of the destination
        await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _pendingDestination.Latitude, _pendingDestination.Longitude, destName);

#if DEBUG
        // --- NEW: Start Simulation ---
        if (_currentRoutePoints != null && _currentRoutePoints.Any())
        {
            await SimulateMovementAlongRouteAsync();
        }
#endif
    }
    private async void OnResetDestinationClicked(object sender, EventArgs e)
    {
        // 1. Reset UI elements
        ResetDestButton.IsVisible = false;
        ConfirmDestButton.IsVisible = true;
        DestinationSearchBar.IsReadOnly = false;
        StartNavButton.IsVisible = true;
        AdminInstructionBanner.IsVisible = true;
        _routeIsActive = false;
        _isSimulating = false;

        // 2. Remove the navigation route (Polyline)
        if (_activeRouteLine != null)
        {
            LiveMap.MapElements.Remove(_activeRouteLine);
            _activeRouteLine = null;
        }

        await _signalRService.CancelGroupNavigation(GroupNameLabel.Text);

        // Note: The destination MapPinViewModel remains in the collection, so the pin stays on the map!

        // 3. Re-adjust the camera
        FitMapToBounds();
    }

    private async void OnNavigationStarted(double destLat, double destLng, string destName)
    {
        if (_routeIsActive && _amIAdmin) return; // Admin already processed this locally

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _activeDestination = new Location(destLat, destLng);
            _routeIsActive = true;

            UpdateDestinationPin(_activeDestination, destName);

            var loc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
            if (loc != null)
            {
                // --- MODIFIED: Capture route points ---
                await CalculateAndDrawRoute(loc, _activeDestination);

#if DEBUG
                // --- NEW: Start Simulation ---
                if (_currentRoutePoints != null && _currentRoutePoints.Any())
                {
                    await SimulateMovementAlongRouteAsync();
                }
#endif
            }

            StartNavButton.IsVisible = true;
            AdminInstructionBanner.IsVisible = false;
            FitMapToBounds();
        });
    }

    // --- ROUTE DRAWING ---
    private async Task CalculateAndDrawRoute(Location origin, Location dest)
    {
        try
        {
            var requestBody = new RoutesRequest
            {
                Origin = new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = origin.Latitude, Longitude = origin.Longitude } } },
                Destination = new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = dest.Latitude, Longitude = dest.Longitude } } }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
            request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var routeResult = JsonSerializer.Deserialize<RoutesResponse>(json);

            var mainRoute = routeResult?.Routes?.FirstOrDefault();
            if (mainRoute != null)
            {
                if (_activeRouteLine != null) LiveMap.MapElements.Remove(_activeRouteLine);

                _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 8 };

                _currentRoutePoints = DecodeGooglePolyline(mainRoute.Polyline.EncodedPolyline);
                foreach (var coord in _currentRoutePoints)
                {
                    _activeRouteLine.Geopath.Add(coord);
                }
                LiveMap.MapElements.Add(_activeRouteLine);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Routing Error: {ex.Message}"); }
    }

    private List<Location> DecodeGooglePolyline(string encodedPoints)
    {
        var poly = new List<Location>();
        char[] polyChars = encodedPoints.ToCharArray();
        int index = 0, currentLat = 0, currentLng = 0;

        while (index < polyChars.Length)
        {
            int sum = 0, shifter = 0, b;
            do { b = polyChars[index++] - 63; sum |= (b & 31) << shifter; shifter += 5; } while (b >= 32);
            currentLat += ((sum & 1) == 1 ? ~(sum >> 1) : (sum >> 1));

            sum = 0; shifter = 0;
            do { b = polyChars[index++] - 63; sum |= (b & 31) << shifter; shifter += 5; } while (b >= 32);
            currentLng += ((sum & 1) == 1 ? ~(sum >> 1) : (sum >> 1));

            poly.Add(new Location(currentLat / 100000.0, currentLng / 100000.0));
        }
        return poly;
    }

    // --- TRACKING LOGIC ---
    private async void InitializeLocalTrackingAsync()
    {
        try
        {
            var locationRequest = new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(5));
            var currentLocation = await Geolocation.Default.GetLocationAsync(locationRequest);

            if (currentLocation != null)
            {
                _lastKnownLocation = currentLocation;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    // Collect all colors used by other users (excluding "You")
                    var usedColors = MapPins
                        .Where(pin => pin.Username != "You")
                        .Select(pin => pin.PinColor)
                        .ToHashSet();

                    // Generate a unique random color
                    Color uniqueColor;
                    var rand = new Random();
                    do
                    {
                        uniqueColor = Color.FromRgb(rand.Next(50, 230), rand.Next(50, 230), rand.Next(50, 230));
                    } while (usedColors.Contains(uniqueColor));

                    // Assign the unique color to your pin
                    _myPinVm = new RiderPin(MapPinClicked)
                    {
                        Username = "You",
                        Speed = "0 mph",
                        Location = currentLocation,
                        PinColor = uniqueColor,
                        ZIndex = 100F, // Ensure your pin is on top

                    };
                    MapPins.Add(_myPinVm);
                    FitMapToBounds();

                });



#if ANDROID
                // Keep tracking alive in the background
                var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(SpeedyCompass.Platforms.Android.AndroidLocationService));
                Android.App.Application.Context.StartForegroundService(intent);
#endif

                StartTrackingLoop();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GPS Init Error: {ex.Message}"); }
    }

    private async void StartTrackingLoop()
    {
        _isTracking = true;
        while (_isTracking)
        {
            try
            {
                // --> NEW: Only fetch real GPS if we aren't running the simulation loop
                if (!_isSimulating)
                {
                    var location = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(5)));
                    if (location != null)
                    {
                        double speedMph = (location.Speed ?? 0) * 2.23694;
                        double distanceThreshold = 5 + speedMph;

                        double distanceMoved = _lastKnownLocation == null
                        ? double.MaxValue
                        : Location.CalculateDistance(_lastKnownLocation, location, DistanceUnits.Kilometers) * 1000;

                        // 1.Calculate heading BEFORE updating the UI
                        if (distanceMoved >= distanceThreshold && _lastKnownLocation != null)
                        {
                            _currentHeading = (location.Course.HasValue && location.Course.Value > 0)
                                ? location.Course.Value
                                : CalculateBearing(_lastKnownLocation, location);
                        }

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (_myPinVm != null)
                            {
                                _myPinVm.Location = location;
                                _myPinVm.Speed = $"{Math.Round(speedMph)} mph";

                                // 2. Pass the heading to the ViewModel so the CustomMapHandler can rotate the camera natively!
                                _myPinVm.Heading = _currentHeading;
                            }
                        });

                        if (distanceMoved >= distanceThreshold)
                        {
                            _lastKnownLocation = location;
                            await _signalRService.UpdateLocation(GroupNameLabel.Text, location.Latitude, location.Longitude, _currentHeading);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Tracking Error: {ex.Message}"); }

            await Task.Delay(2000);
        }
    }

    private void OnRiderLocationUpdated(string riderId, double lat, double lng, double heading)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var newLoc = new Location(lat, lng);

            if (_riderViewModels.TryGetValue(riderId, out var existingVm))
            {
                // Update remote user via DataBinding
                existingVm.Location = newLoc;
                existingVm.Speed = "Active";
            }
            else
            {
                Color randomColor = Color.FromRgb((byte)_randomColorGen.Next(50, 230), (byte)_randomColorGen.Next(50, 230), (byte)_randomColorGen.Next(50, 230));
                var newVm = new RiderPin(MapPinClicked)
                {
                    Username = riderId,
                    Speed = "Active",
                    Location = newLoc,
                    PinColor = randomColor,
                    ZIndex = 50F // Ensure remote users are below "You"
                };

                _riderViewModels.TryAdd(riderId, newVm);
                MapPins.Add(newVm);
            }

            //FitMapToBounds();
        });
    }

    private void FitMapToBounds()
    {
        if (MapPins.Count == 0) return;

        double minLat = double.MaxValue, minLng = double.MaxValue;
        double maxLat = double.MinValue, maxLng = double.MinValue;

        // Loop over the Data Models instead of the Map Elements directly
        foreach (var pin in MapPins)
        {
            if (pin.Location.Latitude < minLat) minLat = pin.Location.Latitude;
            if (pin.Location.Latitude > maxLat) maxLat = pin.Location.Latitude;
            if (pin.Location.Longitude < minLng) minLng = pin.Location.Longitude;
            if (pin.Location.Longitude > maxLng) maxLng = pin.Location.Longitude;
        }

        double centerLat = (minLat + maxLat) / 2.0;
        double centerLng = (minLng + maxLng) / 2.0;

        double latDistance = Math.Max(0.01, (maxLat - minLat) * 1.5);
        double lngDistance = Math.Max(0.01, (maxLng - minLng) * 1.5);

        LiveMap.MoveToRegion(new MapSpan(new Location(centerLat, centerLng), latDistance, lngDistance));
    }
    // --- BEARING / ROTATION HELPER ---
    private double CalculateBearing(Location start, Location end)
    {
        double lat1 = start.Latitude * (Math.PI / 180.0);
        double lon1 = start.Longitude * (Math.PI / 180.0);
        double lat2 = end.Latitude * (Math.PI / 180.0);
        double lon2 = end.Longitude * (Math.PI / 180.0);

        double dLon = lon2 - lon1;

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);

        double bearing = Math.Atan2(y, x) * (180.0 / Math.PI);
        return (bearing + 360.0) % 360.0;
    }

    private async void OnLaunchNativeNavClicked(object sender, EventArgs e)
    {
        if (_activeDestination == null) return;

        try
        {
            if (DeviceInfo.Platform == DevicePlatform.Android)
            {
                await Launcher.OpenAsync($"google.navigation:q={_activeDestination.Latitude},{_activeDestination.Longitude}&mode=d");
            }
            else if (DeviceInfo.Platform == DevicePlatform.iOS)
            {
                bool hasGoogleMaps = await Launcher.TryOpenAsync($"comgooglemaps://?daddr={_activeDestination.Latitude},{_activeDestination.Longitude}&directionsmode=driving");
                if (!hasGoogleMaps)
                {
                    await Launcher.OpenAsync($"http://maps.apple.com/?daddr={_activeDestination.Latitude},{_activeDestination.Longitude}&dirflg=d");
                }
            }
        }
        catch (Exception) { await DisplayAlert("Error", "Could not open map.", "OK"); }
    }

    private void OnRosterUpdated(List<Rider> roster)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Riders.Clear();
            foreach (var rider in roster)
            {
                if (rider.Name == _myName) rider.Name += " (You)";
                Riders.Add(rider);
            }
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isTracking = false;

#if ANDROID
        var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(SpeedyCompass.Platforms.Android.AndroidLocationService));
        Android.App.Application.Context.StopService(intent);
#endif

        _signalRService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _signalRService.RosterUpdated -= OnRosterUpdated;
        _signalRService.NavigationStarted -= OnNavigationStarted;
        _signalRService.RiderLocationUpdated -= OnRiderLocationUpdated;
    }
    private void MapPinClicked(RiderPin pin)
    {
        // Handle pin click
    }
    private async Task SimulateMovementAlongRouteAsync()
    {
        if (_currentRoutePoints == null || _currentRoutePoints.Count == 0) return;

        await Task.Delay(2000); // 2-second delay as requested
        _isSimulating = true;   // Flag to pause real GPS fetching

        foreach (var point in _currentRoutePoints)
        {
            if (!_isTracking || !_isSimulating) break; // Stop if user left the page

            // 1. Calculate the simulated heading
            double fakeHeading = _lastKnownLocation != null
                ? CalculateBearing(_lastKnownLocation, point)
                : 0;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_myPinVm != null)
                {
                    _myPinVm.Location = point;
                    _myPinVm.Speed = "Simulated";
                    // 2. Pass the heading to the ViewModel so the CustomMapHandler can rotate the camera natively!
                    _myPinVm.Heading = fakeHeading;
                }
                //FitMapToBounds(); // Optional: keeps camera following the action
            });

            _lastKnownLocation = point;

            // Broadcast fake movement to the group!
            await _signalRService.UpdateLocation(GroupNameLabel.Text, point.Latitude, point.Longitude, fakeHeading);

            await Task.Delay(2000); // Move to the next point every 1 second
        }

        _isSimulating = false;
    }
    private void OnNavigationCancelled()
    {
        if (_amIAdmin) return; // Admin already processed this locally

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _routeIsActive = false;
            _isSimulating = false;
            StartNavButton.IsVisible = false;

            // Remove the navigation route
            if (_activeRouteLine != null)
            {
                LiveMap.MapElements.Remove(_activeRouteLine);
                _activeRouteLine = null;
            }

            // Keep the destination pin as requested, but re-adjust camera to fit everyone
            FitMapToBounds();
        });
    }
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = e.NewTextValue;

        // Don't search until they've typed at least 3 characters
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
        {
            // Update: Toggle the Frame instead of the ListView
            SuggestionsFrame.IsVisible = false;
            return;
        }

        // Cancel the previous debounce timer
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();

        try
        {
            // Increase the API hit counter
            _autocompleteApiHits++;

            // Call Google Places API (New) - Autocomplete endpoint
            var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

            var reqBody = new AutocompleteRequest { Input = query };
            request.Content = new StringContent(JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AutocompleteResponse>(responseBody);

            if (result != null && result.Suggestions != null && result.Suggestions.Any())
            {
                // Map to our UI model so XAML binding still works perfectly
                var displayList = result.Suggestions
                    .Where(s => s.PlacePrediction != null)
                    .Select(s => new UIPlaceSuggestion
                    {
                        Description = s.PlacePrediction.Text.Text,
                        PlaceId = s.PlacePrediction.PlaceId
                    }).ToList();

                SuggestionsListView.ItemsSource = displayList;

                // Update: Toggle the Frame instead of the ListView
                SuggestionsFrame.IsVisible = true;
            }

            // Wait for a short duration to debounce rapid requests (e.g., 300ms)
            await Task.Delay(300, _debounceCts.Token);

            // Check if this is the latest request based on the counter
            if (_autocompleteApiHits != _autocompleteApiHits)
                return;

            // Here, you can safely use the result for the latest request
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
        }
    }
    // UPDATED: Changed SelectionChangedEventArgs to SelectedItemChangedEventArgs for ListView compatibility
    private async void OnSuggestionSelected(object sender, SelectedItemChangedEventArgs e)
    {
        // UPDATED: Use e.SelectedItem instead of e.CurrentSelection
        if (e.SelectedItem is UIPlaceSuggestion selectedPlace)
        {
            // 1. Hide the suggestions dropdown frame and update the search bar text
            SuggestionsFrame.IsVisible = false;
            DestinationSearchBar.Text = selectedPlace.Description;

            try
            {
                // 2. Fetch the exact coordinates using the Place API (New)
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://places.googleapis.com/v1/places/{selectedPlace.PlaceId}");
                request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

                // FieldMask is REQUIRED in the New API to tell Google exactly what data you want to retrieve
                request.Headers.Add("X-Goog-FieldMask", "location");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                var details = JsonSerializer.Deserialize<PlaceDetailsResponse>(responseBody);

                if (details?.Location != null)
                {
                    double lat = details.Location.Latitude;
                    double lng = details.Location.Longitude;

                    _pendingDestination = new Location(lat, lng);

                    // 3. Clear old preview pins
                    LiveMap.Pins.Clear();

                    // 4. Add new pin to map
                    var pin = new Pin
                    {
                        Label = selectedPlace.Description,
                        Type = PinType.Place,
                        Location = _pendingDestination
                    };
                    LiveMap.Pins.Add(pin);

                    // 5. Move map camera view to focus on the destination
                    var mapSpan = MapSpan.FromCenterAndRadius(_pendingDestination, Distance.FromMiles(1));
                    LiveMap.MoveToRegion(mapSpan);

                    // 6. Enable the broadcast button
                    ConfirmDestButton.IsEnabled = true;
                    ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Could not fetch location details.", "OK");
                System.Diagnostics.Debug.WriteLine($"Details Error: {ex.Message}");
            }

            // Clear selection so the user can tap it again if needed
            SuggestionsListView.SelectedItem = null;
        }
    }

    // Call this when navigation actually starts (e.g., inside OnConfirmDestinationClicked)
    private void EnableNavigationUI()
    {
        OverviewButton.IsVisible = true;
        ResumeNavButton.IsVisible = false;
        if (_myPinVm != null) _myPinVm.IsAutoCentering = true;
    }

    private async void OnOverviewClicked(object sender, EventArgs e)
    {
        if (_myPinVm == null) return;

        // 1. Swap Buttons
        OverviewButton.IsVisible = false;
        ResumeNavButton.IsVisible = true;

        // 2. Tell the Android handler to STOP forcing the camera to follow you
        _myPinVm.IsAutoCentering = false;

        // 3. Reset the 3D tilt and rotation back to a flat, top-down view
        await LiveMap.RotateTo(0, 500, Microsoft.Maui.Easing.SinInOut);
        LiveMap.Scale = 1.0;

        // 4. Zoom out to show everyone
        FitMapToBounds();
    }

    private void OnResumeNavClicked(object sender, EventArgs e)
    {
        if (_myPinVm == null) return;

        // 1. Swap Buttons
        ResumeNavButton.IsVisible = false;
        OverviewButton.IsVisible = true;

        // 2. Tell the Android Handler to take control again!
        // As soon as this is true, the very next GPS tick will automatically 
        // swoop the camera back down into the 3D navigation view.
        _myPinVm.IsAutoCentering = true;
    }

    private void AddRandomMapPins(int count = 5)
    {
        MapPins.Clear();
        double baseLat = 22.574354;
        double baseLng = 88.362873;
        var rand = new Random();

        for (int i = 0; i < count; i++)
        {
            // Generate small random offsets (within ~0.005 degrees)
            double latOffset = (rand.NextDouble() - 0.5) * 0.01;
            double lngOffset = (rand.NextDouble() - 0.5) * 0.01;

            var location = new Location(baseLat + latOffset, baseLng + lngOffset);

            var pin = new RiderPin(MapPinClicked)
            {
                Username = $"Rider_{i + 1}",
                Speed = $"{rand.Next(5, 30)} mph",
                Location = location,
                PinColor = Color.FromRgb(rand.Next(50, 230), rand.Next(50, 230), rand.Next(50, 230)),
                ImageSource = "clipart2240358"
            };

            MapPins.Add(pin);
        }
    }
}