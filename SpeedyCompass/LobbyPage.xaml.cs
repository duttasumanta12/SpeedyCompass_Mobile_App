using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Extensions.Configuration;
using SpeedyCompass.Services;
using SpeedyCompass.Controls;
using Microsoft.Maui.ApplicationModel;

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

    public LobbyPage(SignalRService signalRService, string groupName)
    {
        InitializeComponent();

        // Ensure UI elements bind to this code-behind class
        BindingContext = this;

        AddRandomMapPins();

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

        ConfirmDestButton.IsEnabled = false;
        AdminSearchUI.IsVisible = false;
        AdminInstructionBanner.IsVisible = false;
        _routeIsActive = true;

        string destName = DestinationSearchBar.Text ?? "Destination";

        await CalculateAndDrawRoute(_lastKnownLocation, _pendingDestination);

        _activeDestination = _pendingDestination;
        StartNavButton.IsVisible = true;
        FitMapToBounds();

        // Inform the entire group of the destination
        await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _pendingDestination.Latitude, _pendingDestination.Longitude, destName);
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
                await CalculateAndDrawRoute(loc, _activeDestination);
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
                foreach (var coord in DecodeGooglePolyline(mainRoute.Polyline.EncodedPolyline))
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
                    if (_myPinVm == null)
                    {
                        // Add ourselves to the MVVM collection
                        _myPinVm = new RiderPin(MapPinClicked)
                        {
                            Username = "You",
                            Speed = "0 mph",
                            Location = currentLocation,
                            PinColor = Colors.DodgerBlue,
                            ImageSource = "icon_type_four"
                        };
                        MapPins.Add(_myPinVm);
                        FitMapToBounds();
                    }
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
                var location = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(5)));
                if (location != null)
                {
                    double speedMph = (location.Speed ?? 0) * 2.23694;
                    double distanceThreshold = 5 + speedMph;

                    double distanceMoved = _lastKnownLocation == null
                        ? double.MaxValue
                        : Location.CalculateDistance(_lastKnownLocation, location, DistanceUnits.Kilometers) * 1000;

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_myPinVm != null)
                        {
                            // MVVM DATA BINDING MAGIC: We just update the properties. The Map redraws it automatically!
                            _myPinVm.Location = location;
                            _myPinVm.Speed = $"{Math.Round(speedMph)} mph";
                        }
                    });

                    if (distanceMoved >= distanceThreshold)
                    {
                        _lastKnownLocation = location;
                        await _signalRService.UpdateLocation(GroupNameLabel.Text, location.Latitude, location.Longitude, location.Course ?? 0);
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
                var newVm = new RiderPin(MapPinClicked) { Username = riderId, Speed = "Active", Location = newLoc, PinColor = randomColor, ImageSource = "icon_type_four" };

                _riderViewModels.TryAdd(riderId, newVm);
                MapPins.Add(newVm);
            }

            FitMapToBounds();
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
                ImageSource = "icon_type_four"
            };

            MapPins.Add(pin);
        }
    }
}