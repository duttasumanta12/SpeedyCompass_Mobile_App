#if ANDROID
using AndroidX.ConstraintLayout.Core.Motion.Utils;
#endif
using Microsoft.Extensions.Configuration;
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
using Easing = Microsoft.Maui.Easing;
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

    // NEW FLAG: Tracks if the user intentionally wants to delete/leave the group
    private bool _isLeavingGroupPermanently = false;
    // FIX: Flag to prevent the suggestion list from reopening
    private bool _isSelectingLocation = false;
    // --- NEW: PTT State ---
    private readonly HardwareButtonService _hwButtonService;
    private string _currentSpeaker = string.Empty;
    // THE FIX: Use a Reliable Token instead of IDispatcherTimer
    private CancellationTokenSource _pttCts;
    private int _pttTimeRemaining;

    public LobbyPage(SignalRService signalRService, string groupName)
    {
        InitializeComponent();

        // Ensure UI elements bind to this code-behind class
        BindingContext = this;

        _signalRService = signalRService;

#if ANDROID
        MainActivity.OnPiPModeChangedEvent += HandlePiPModeChanged;
#endif

        // Fetch OS-Specific tracker from MAUI Services directly!
        // This prevents constructor errors when navigating from MainPage.
#if ANDROID
        _locationTracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
        if (_locationTracker != null)
        {
            _locationTracker.LocationUpdated += OnLocalLocationPushedFromBackground;
        }
#endif

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
        _signalRService.AlertReceived += OnAlertReceived;
        _signalRService.DestinationSet += OnDestinationSet;
        _signalRService.UserJoinedAlert += OnUserJoined;
        _signalRService.UserLeftAlert += OnUserLeft;
        _signalRService.GroupDeleted += OnGroupDeleted;
        // Subscribe to SignalR PTT Events
        _signalRService.PttLocked += OnPttLocked;
        _signalRService.PttDenied += OnPttDenied;
        _signalRService.PttReleased += OnPttReleased;

        // Fetch Hardware Button Service and subscribe
        _hwButtonService = IPlatformApplication.Current?.Services.GetService<HardwareButtonService>();
        if (_hwButtonService != null)
        {
            _hwButtonService.PttPressed += OnHardwarePttPressed;
            _hwButtonService.PttReleased += OnHardwarePttReleased;
        }

    }
    // --- THE FIX: RELIABLE 30-SEC TIMEOUT LOOP ---
    private async Task RunPttTimeoutAsync(CancellationToken token)
    {
        _pttTimeRemaining = 30; // Max 30 seconds per request

        try
        {
            while (_pttTimeRemaining > 0 && !token.IsCancellationRequested)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PttCountdownLabel.Text = $"Auto-closing in {_pttTimeRemaining}s...";
                });

                await Task.Delay(1000, token);
                _pttTimeRemaining--;
            }

            if (_pttTimeRemaining <= 0 && !token.IsCancellationRequested)
            {
                // Timeout reached!
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PttCountdownLabel.Text = "Maximum time reached!";
                    _ = TextToSpeech.Default.SpeakAsync("Microphone closed.");
                });

                if (_currentSpeaker == _myName)
                {
                    await _signalRService.ReleasePtt(GroupNameLabel.Text, _myName);
                }
            }
        }
        catch (TaskCanceledException) { /* Ignored on early release */ }
    }
    // --- NEW: PTT HARDWARE TRIGGERS ---
    private async void OnHardwarePttPressed(object sender, EventArgs e)
    {
        // Ignore if we are already the speaker
        if (_currentSpeaker == _myName) return;

        // Ask the server for the mic lock!
        await _signalRService.RequestPtt(GroupNameLabel.Text, _myName);
    }

    private async void OnHardwarePttReleased(object sender, EventArgs e)
    {
        // If we let go of the button, and we hold the lock, release it!
        if (_currentSpeaker == _myName)
        {
            await _signalRService.ReleasePtt(GroupNameLabel.Text, _myName);
        }
    }

    // --- NEW: PTT SERVER RESPONSES ---
    private void OnPttLocked(string speakerName)
    {
        _currentSpeaker = speakerName;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            PttOverlay.IsVisible = true;

            if (speakerName == _myName)
            {
                // WE got the lock!
                PttStatusLabel.Text = "MIC OPEN";
                PttStatusLabel.TextColor = Colors.MediumSeaGreen;
                PttSpeakerLabel.Text = "You can now speak to the group.";

                // Start the highly reliable 15-Second Timer Loop
                _pttCts?.Cancel();
                _pttCts = new CancellationTokenSource();
                PttCountdownLabel.IsVisible = true;
                _ = RunPttTimeoutAsync(_pttCts.Token);

                // Tactile feedback (buzz) and Voice
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));
                _ = TextToSpeech.Default.SpeakAsync("You can now speak.");
            }
            else
            {
                // SOMEONE ELSE got the lock!
                _pttCts?.Cancel(); // Ensure our timer isn't running
                PttCountdownLabel.IsVisible = false;

                PttStatusLabel.Text = "RECEIVING";
                PttStatusLabel.TextColor = Colors.DodgerBlue;
                PttSpeakerLabel.Text = $"{speakerName} is speaking...";
            }
        });
    }

    private void OnPttDenied(string activeSpeaker)
    {
        // We tried to talk, but someone else is already talking!
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _ = TextToSpeech.Default.SpeakAsync("Channel busy.");
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500)); // Longer warning buzz
        });
    }

    private void OnPttReleased()
    {
        _currentSpeaker = string.Empty;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _pttCts?.Cancel(); // ALWAYS kill the timer on release!
            PttOverlay.IsVisible = false;
            PttCountdownLabel.IsVisible = false;
        });
    }

    // --- NEW: UI UPDATE FROM BACKGROUND SERVICE ---
    private void OnLocalLocationPushedFromBackground(object sender, LocalLocationUpdate e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            LocationDisabledOverlay.IsVisible = false;

            if (_myPinVm != null)
            {
                _myPinVm.Location = e.Location;
                _myPinVm.Speed = $"{Math.Round(e.SpeedMph)} mph";
            }

            if (_routeIsActive)
            {
                await LiveMap.RotateTo(360 - e.Heading, 500, Easing.SinInOut);
                LiveMap.Scale = 1.4;
            }
        });
    }

    // 3. Add the toggle logic:
    private void HandlePiPModeChanged(bool isPipMode)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (isPipMode)
            {
                // Entering PiP: Hide standard UI, show Minimal Telemetry
                TabRoster.IsVisible = false;
                TabMap.IsVisible = false;
                DestinationSearchBar.IsVisible = false;
                ActionDrawer.IsVisible = false;

                // Keep the map rendering in the background if you want, or hide it to save GPU
                MapView.IsVisible = false;

                // Populate telemetry data
                PipRiderCountLabel.Text = $"{Riders.Count(r => r.IsOnline)}/{Riders.Count} Riders";
                PipSpeedLabel.Text = _myPinVm?.Speed ?? "0 mph";

                PipOverlayGrid.IsVisible = true;
            }
            else
            {
                // Exiting PiP (App maximized): Restore standard UI
                PipOverlayGrid.IsVisible = false;

                TabRoster.IsVisible = true;
                TabMap.IsVisible = true;
                DestinationSearchBar.IsVisible = true;
                MapView.IsVisible = true;

                if (_routeIsActive) ActionDrawer.IsVisible = true;
            }
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_hasJoined) return;

        try
        {
            // FIX 1: Because SignalR started on the MainPage, the "Connected" event already fired in the past!
            // We manually trigger the UI update to clear the "Connecting..." label.
            OnConnectionStatusChanged("Connected", Colors.MediumSeaGreen);

            // FIX 2: Manually ask the server for the roster! 
            // The RosterUpdated event may have broadcasted before this page finished loading,
            // so we fetch it now to ensure it's perfectly in sync.
            var roster = await _signalRService.GetGroupRoster(GroupNameLabel.Text);
            if (roster != null)
            {
                OnRosterUpdated(roster);
            }

            _hasJoined = true;

            AdminSettingsBtn.IsVisible = _amIAdmin;

            InitializeLocalTrackingAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not load lobby: {ex.Message}", "OK");
            await Navigation.PopAsync();
        }
    }
    // --- NEW: ADMIN SETTINGS UI EVENTS ---
    private void OnAdminSettingsClicked(object sender, EventArgs e)
    {
        AdminSettingsOverlay.IsVisible = true;
    }
    private void OnCloseSettingsClicked(object sender, EventArgs e)
    {
        AdminSettingsOverlay.IsVisible = false;
    }
    private void OnLagSliderChanged(object sender, ValueChangedEventArgs e)
    {
        // Round to nearest 50 meters for clean UX
        double roundedValue = Math.Round(e.NewValue / 50.0) * 50;
        LagSlider.Value = roundedValue; // Snap the slider
        LagValueLabel.Text = $"{roundedValue}m";
    }
    private void OnSplinterSliderChanged(object sender, ValueChangedEventArgs e)
    {
        // Round to nearest 100 meters
        double roundedValue = Math.Round(e.NewValue / 100.0) * 100;
        SplinterSlider.Value = roundedValue; // Snap the slider
        SplinterValueLabel.Text = $"{roundedValue}m";
    }
    private async void OnSaveSettingsClicked(object sender, EventArgs e)
    {
        int maxLag = (int)LagSlider.Value;
        int splinterDist = (int)SplinterSlider.Value;
        int maxSize = (int)SizeSlider.Value;

        // Push all three settings to Cosmos DB via SignalR
        await _signalRService.UpdateGroupSettings(GroupNameLabel.Text, maxLag, splinterDist, maxSize);

        AdminSettingsOverlay.IsVisible = false;
        Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100));
    }

    private void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            StatusLabel.TextColor = color;
            StatusDot.BackgroundColor = color;
            // FIX: Force the layout engine to recalculate and repaint this specific UI block
            if (StatusLabel.Parent is View parentView)
            {
                parentView.InvalidateMeasure();
            }
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

        ConfirmDestButton.IsVisible = false;
        ResetDestButton.IsVisible = true;
        DestinationSearchBar.IsReadOnly = true;
        AdminInstructionBanner.IsVisible = false;

        string destName = DestinationSearchBar.Text ?? "Destination";
        _activeDestination = _pendingDestination;

        // Switch the Admin's view to the Roster tab automatically to see the "Start Journey" button
        OnTabClicked(TabRoster, EventArgs.Empty);

        // Broadcast the pending destination to everyone's Roster (does NOT start navigation yet)
        await _signalRService.SetGroupDestination(GroupNameLabel.Text, _pendingDestination.Latitude, _pendingDestination.Longitude, destName);
    }
    private async void OnResetDestinationClicked(object sender, EventArgs e)
    {
        ResetDestButton.IsVisible = false;
        ConfirmDestButton.IsVisible = true;
        DestinationSearchBar.IsReadOnly = false;
        StartNavButton.IsVisible = false;
        ActionDrawer.IsVisible = false;
        MinimizePanelButton.IsVisible = false;
        AdminInstructionBanner.IsVisible = true;
        PendingDestinationFrame.IsVisible = false; // Hide Roster dashboard
        _routeIsActive = false;
        _isSimulating = false;

        if (_activeRouteLine != null)
        {
            LiveMap.MapElements.Remove(_activeRouteLine);
            _activeRouteLine = null;
        }

        await _signalRService.CancelGroupNavigation(GroupNameLabel.Text);
        FitMapToBounds();
    }

    private async void OnNavigationStarted(double destLat, double destLng, string destName)
    {
        _routeIsActive = true;
        _activeDestination = new Location(destLat, destLng);

        // Start the background GPS Tracker
        _locationTracker?.StartTracking(GroupNameLabel.Text);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 🚀 FORCE EVERYONE TO THE MAP TAB AUTOMATICALLY
            OnTabClicked(TabMap, EventArgs.Empty);

            // FIX: Ensure StartNavButton hides, while Action panels show
            StartNavButton.IsVisible = true;
            //ActionDrawer.IsVisible = true;
            ActionDrawer.TranslationY = 300;
            FloatingControlsLayout.TranslationY = 0;
            MinimizePanelButton.IsVisible = true;
            AdminInstructionBanner.IsVisible = false;

#if ANDROID
            MainActivity.IsInNavigationMode = true;
#endif
            UpdateDestinationPin(_activeDestination, destName);
        });

        var loc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
        if (loc != null)
        {
            await CalculateAndDrawRoute(loc, _activeDestination);
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());

#if DEBUG
            if (_currentRoutePoints != null && _currentRoutePoints.Any())
            {
                _ = SimulateMovementAlongRouteAsync();
            }
#endif
        }

        _ = TextToSpeech.Default.SpeakAsync($"Navigation started to {destName}. Ride safe!");
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

    // --- REPLACED TRACKING LOGIC ---
    private async void InitializeLocalTrackingAsync()
    {
        try
        {
            // 1. Explicitly check for permissions first
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
                if (status != PermissionStatus.Granted)
                {
                    MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.IsVisible = true);
                    return;
                }
            }

            // --- THE FIX: We must explicitly ask for Microphone access! ---
            var micStatus = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (micStatus != PermissionStatus.Granted)
            {
                await Permissions.RequestAsync<Permissions.Microphone>();
            }

            // 2. We only fetch ONE location here to center the map initially.
            // The Background Service handles all continuous tracking now!
            var locationRequest = new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(5));
            var currentLocation = await Geolocation.Default.GetLocationAsync(locationRequest);

            if (currentLocation != null)
            {
                _lastKnownLocation = currentLocation;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    LocationDisabledOverlay.IsVisible = false;

                    var usedColors = MapPins.Where(pin => pin.Username != "You").Select(pin => pin.PinColor).ToHashSet();
                    Color uniqueColor;
                    var rand = new Random();
                    do { uniqueColor = Color.FromRgb(rand.Next(50, 230), rand.Next(50, 230), rand.Next(50, 230)); } while (usedColors.Contains(uniqueColor));

                    if (_myPinVm == null)
                    {
                        _myPinVm = new RiderPin(MapPinClicked)
                        {
                            Username = "You",
                            Speed = "0 mph",
                            Location = currentLocation,
                            PinColor = uniqueColor,
                            ZIndex = 100F,
                            ImageSource = "clipart2240358"
                        };
                        MapPins.Add(_myPinVm);
                        FitMapToBounds();
                    }
                });
            }
        }
        catch (FeatureNotEnabledException)
        {
            MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.IsVisible = true);
        }
        catch (PermissionException)
        {
            MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.IsVisible = true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GPS Init Error: {ex.Message}");
        }
    }

    private async void StartTrackingLoop()
    {
        if (_isTracking) return; // Prevent multiple loops running concurrently

        _isTracking = true;
        while (_isTracking)
        {
            try
            {
                if (!_isSimulating)
                {
                    var location = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(5)));
                    if (location != null)
                    {
                        // Ensure overlay is hidden if location successfully fetched
                        MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.IsVisible = false);

                        double speedMph = (location.Speed ?? 0) * 2.23694;
                        double distanceThreshold = 5 + speedMph;

                        double distanceMoved = _lastKnownLocation == null
                            ? double.MaxValue
                            : Location.CalculateDistance(_lastKnownLocation, location, DistanceUnits.Kilometers) * 1000;

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (_myPinVm != null)
                            {
                                _myPinVm.Location = location;
                                _myPinVm.Speed = $"{Math.Round(speedMph)} mph";
                            }
                        });

                        if (distanceMoved >= distanceThreshold)
                        {
                            if (_lastKnownLocation != null)
                            {
                                _currentHeading = (location.Course.HasValue && location.Course.Value > 0)
                                    ? location.Course.Value
                                    : CalculateBearing(_lastKnownLocation, location);

                                MainThread.BeginInvokeOnMainThread(async () =>
                                {
                                    if (_routeIsActive)
                                    {
                                        await LiveMap.RotateTo(360 - _currentHeading, 500, Microsoft.Maui.Easing.SinInOut);
                                        LiveMap.Scale = 1.4;
                                    }
                                    else
                                    {
                                        await LiveMap.RotateTo(0, 500, Microsoft.Maui.Easing.SinInOut);
                                        LiveMap.Scale = 1.0;
                                    }
                                });
                            }
                            _lastKnownLocation = location;
                            await _signalRService.UpdateLocation(GroupNameLabel.Text, _myPinVm.Username, location.Latitude, location.Longitude, location.Course ?? _currentHeading);
                        }
                    }
                }
            }
            catch (FeatureNotEnabledException) // Catch if GPS hardware is turned off MID-RIDE
            {
                MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.IsVisible = true);
                _isTracking = false; // Kill the tracking loop, wait for user to retry
            }
            catch (PermissionException) // Catch if permission is revoked MID-RIDE
            {
                MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.IsVisible = true);
                _isTracking = false; // Kill the tracking loop, wait for user to retry
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tracking Error: {ex.Message}");
            }

            if (_isTracking) await Task.Delay(2000);
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
            // FIX: Build a fresh collection in memory instead of using .Clear() and .Add()
            var updatedRiders = new ObservableCollection<Rider>();

            foreach (var rider in roster)
            {
                if (rider.Name == _myName) rider.Name += " (You)";

                if (!rider.IsOnline)
                {
                    rider.Name += " (Offline)";
                    // Optional: You can also change the role color to dim it out
                    // rider.r = Colors.DimGray; 
                }

                updatedRiders.Add(rider);
            }

            // FIX: Reassigning ItemsSource completely breaks the render cache and forces an instant UI update
            Riders = updatedRiders;
            RidersCollectionView.ItemsSource = Riders;

            // FIX: Ensure the PiP overlay numbers update dynamically as well!
            PipRiderCountLabel.Text = $"{Riders.Count(r => r.IsOnline)}/{Riders.Count} Riders";
        });
    }

    protected async override void OnDisappearing()
    {
        base.OnDisappearing();

        // THE MAGIC FIX: Determine if we are navigating away (Back button) vs minimizing the app
        // If the page is no longer in the stack, it was popped via the Back button.
        bool isPopping = Navigation?.NavigationStack?.Contains(this) == false;

        if (isPopping)
        {
            // --- THE USER IS ACTUALLY LEAVING THIS PAGE (BACK BUTTON) ---

            // 1. Unsubscribe from ALL events to prevent memory leaks
            _signalRService.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _signalRService.RosterUpdated -= OnRosterUpdated;
            _signalRService.NavigationStarted -= OnNavigationStarted;
            _signalRService.RiderLocationUpdated -= OnRiderLocationUpdated;
            _signalRService.NavigationCancelled -= OnNavigationCancelled;
            _signalRService.DestinationSet -= OnDestinationSet; // Re-added from previous context if you had it
            _signalRService.UserJoinedAlert -= OnUserJoined;
            _signalRService.UserLeftAlert -= OnUserLeft;
            _signalRService.GroupDeleted -= OnGroupDeleted;
            _signalRService.PttLocked -= OnPttLocked;
            _signalRService.PttDenied -= OnPttDenied;
            _signalRService.PttReleased -= OnPttReleased;

            if (_hwButtonService != null)
            {
                _hwButtonService.PttPressed -= OnHardwarePttPressed;
                _hwButtonService.PttReleased -= OnHardwarePttReleased;
            }

            // 2. Stop all location tracking (Using our clean cross-platform interface)
            _isTracking = false;
            _isSimulating = false;
            _locationTracker?.StopTracking();

#if ANDROID
            // 3. Clean up Android Picture-in-Picture mode
            MainActivity.OnPiPModeChangedEvent -= HandlePiPModeChanged;
            MainActivity.IsInNavigationMode = false;
#endif

            // 4. Handle Server Teardown (Only if they didn't hit "Leave Group" explicitly)
            if (!_isLeavingGroupPermanently)
            {
                // Tell the server we stepped back to the MainPage. 
                // Note: The backend Hub's LeaveLobby() method automatically pauses 
                // the route for everyone if an Admin leaves, so we don't need redundant code here!
                _ = _signalRService.LeaveLobby();
            }
        }
        else
        {
            // --- THE USER LOCKED THE SCREEN OR MINIMIZED ---
            // Do NOTHING! 
            // - The OS LocationManager will keep firing in the background.
            // - SignalR stays connected.
            // - The UI will instantly update when the screen turns back on.
        }
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

        // THE FIX: DO NOT stop the location tracker. The Foreground Service keeps the app alive 
        // in the background when the screen is locked! Just tell it to ignore hardware GPS.
        if (_locationTracker != null) _locationTracker.IsSimulating = true;

        foreach (var point in _currentRoutePoints)
        {
            if (!_routeIsActive || !_isSimulating) break; // Stop if user left the page

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
            await _signalRService.UpdateLocation(GroupNameLabel.Text, _myName, point.Latitude, point.Longitude, fakeHeading);

            await Task.Delay(2000); // Move to the next point every 1 second
        }

        _isSimulating = false;
    }
    private void OnNavigationCancelled()
    {
        // FIX: Removed the 'if (_amIAdmin) return;' line!
        // The Admin needs their UI to reset just like everyone else when the route is cancelled.

#if ANDROID
        // DISABLE PiP shrinking since the route ended
        MainActivity.IsInNavigationMode = false;
#endif

        // STOP THE BACKGROUND ENGINE SAFELY
        _locationTracker?.StopTracking();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _routeIsActive = false;
            _isSimulating = false;

            // Hide active navigation UI
            StartNavButton.IsVisible = false;
            ActionDrawer.IsVisible = false;
            MinimizePanelButton.IsVisible = false;
            PendingDestinationFrame.IsVisible = false;

            // Clear Map Line
            if (_activeRouteLine != null)
            {
                LiveMap.MapElements.Remove(_activeRouteLine);
                _activeRouteLine = null;
            }

            // Clear Destination Pin
            var oldDest = LiveMap.Pins.FirstOrDefault(p => p.Label != "You" && p.Type == PinType.Place);
            if (oldDest != null) LiveMap.Pins.Remove(oldDest);

            FitMapToBounds();
        });
    }
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // FIX: Ignore the event if we are setting the text programmatically
        if (_isSelectingLocation) return;

        if (e.OldTextValue == e.NewTextValue) return;
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
            await Task.Delay(1000, _debounceCts.Token);

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

            // FIX: Temporarily block OnSearchTextChanged while we set the text!
            _isSelectingLocation = true;
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
    private async void OnEmergencyStopClicked(object sender, EventArgs e)
    {
        await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Emergency", _myName);
    }

    private async void OnRefuelStopClicked(object sender, EventArgs e)
    {
        await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Refuel", _myName);
    }

    private async void OnRestStopClicked(object sender, EventArgs e)
    {
        await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Rest", _myName);
    }
    // --- SENSORY ALERT PROCESSOR ---
    private void SetActionButtonsEnabled(bool isEnabled)
    {
        if (ActionButtonsStack == null) return;

        // 1. Loop through all children of the stack (Stop, Refuel, Rest, Overview, PTT)
        foreach (var child in ActionButtonsStack.Children)
        {
            if (child is Button btn)
            {
                btn.IsEnabled = isEnabled;
                // Provide subtle visual dimming when disabled
                btn.Opacity = isEnabled ? 1.0 : 0.4;
            }
        }

        // 2. Also disable the GMAP button so users don't jump out during active safety alerts
        if (StartNavButton != null)
        {
            StartNavButton.IsEnabled = isEnabled;
            StartNavButton.Opacity = isEnabled ? 1.0 : 0.4;
        }
    }


    private async void OnAlertReceived(string alertType, string senderName)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // --- 1. TELEMETRY ALERTS (VOICE & HAPTIC ONLY) ---
            // These happen automatically in the background. We DO NOT freeze the screen.
            if (alertType == "Lagging" || alertType == "Splinter")
            {
                // Trigger a quick buzz so they know an audio prompt is starting
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));

                // Speak the exact math string generated by the server 
                // e.g., "Rahul is 600m behind the group."
                _ = TextToSpeech.Default.SpeakAsync(senderName);

                return; // Exit here so we skip the full-screen visual overlay
            }
            int durationSeconds = 5;
            string voiceMessage = "";

            // 🛑 FREEZE ALL ACTIONS ON SCREEN AT START
            SetActionButtonsEnabled(false);

            // Configure UI based on the alert type
            if (alertType == "Emergency")
            {
                SensoryAlertOverlay.BackgroundColor = Colors.Red;
                AlertTitleLabel.Text = "EMERGENCY STOP!";
                AlertIconLabel.Text = "🛑";
                durationSeconds = 10;
                voiceMessage = $"Emergency Stop triggered by {senderName}. Please pull over safely immediately.";
            }
            else if (alertType == "Refuel")
            {
                SensoryAlertOverlay.BackgroundColor = Colors.DarkOrange;
                AlertTitleLabel.Text = "REFUEL STOP";
                AlertIconLabel.Text = "⛽";
                durationSeconds = 5;
                voiceMessage = $"{senderName} needs a refuel break. Prepare to stop at the next gas station.";
            }
            else if (alertType == "Rest")
            {
                SensoryAlertOverlay.BackgroundColor = Colors.DodgerBlue;
                AlertTitleLabel.Text = "REST STOP";
                AlertIconLabel.Text = "☕";
                durationSeconds = 5;
                voiceMessage = $"{senderName} requested a rest stop. Prepare to pull over soon.";
            }

            AlertSenderLabel.Text = $"Triggered by: {senderName}";
            SensoryAlertOverlay.IsVisible = true;

            // Trigger Voice Alert
            _ = TextToSpeech.Default.SpeakAsync(voiceMessage);

            // Loop Vibration and Blinking Animation
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
                    await SensoryAlertOverlay.FadeTo(0.8, 250);
                    await SensoryAlertOverlay.FadeTo(0.2, 250);
                }
            }
            catch (TaskCanceledException) { }

            // Clean up when done
            Vibration.Default.Cancel();
            SensoryAlertOverlay.IsVisible = false;
            SensoryAlertOverlay.Opacity = 0;

            // 🔓 UNFREEZE ALL ACTIONS AT END
            SetActionButtonsEnabled(true);
        });
    }
    private bool _panelVisible = false;
    private ILocationTracker? _locationTracker;

    // 2. Replace your existing OnMinimizePanelClicked with this updated drawer animation
    private async void OnMinimizePanelClicked(object sender, EventArgs e)
    {
        MinimizePanelButton.IsEnabled = false;

        // Calculate the height securely (fallback to 250 if the UI hasn't fully rendered it yet)
        double drawerHeight = ActionDrawer.Height > 0 ? ActionDrawer.Height : 250;

        // Add a 20px gap to ensure the toggle button sits cleanly ABOVE the drawer without overlapping
        double pushUpAmount = drawerHeight + 20;

        if (_panelVisible)
        {
            // CLOSE ANIMATION: Slide drawer down and drop buttons back
            await Task.WhenAll(
                ActionDrawer.TranslateTo(0, drawerHeight, 250, Easing.CubicIn),
                FloatingControlsLayout.TranslateTo(0, 0, 250, Easing.CubicIn)
            );

            ActionDrawer.IsVisible = false;
            MinimizePanelButton.Text = "🔼";
            _panelVisible = false;
        }
        else
        {
            // OPEN ANIMATION: Prep drawer position, then slide drawer up AND push floating controls up
            ActionDrawer.IsVisible = true;
            if (ActionDrawer.TranslationY == 0) ActionDrawer.TranslationY = drawerHeight;

            await Task.WhenAll(
                ActionDrawer.TranslateTo(0, 0, 250, Easing.CubicOut),
                FloatingControlsLayout.TranslateTo(0, -pushUpAmount, 250, Easing.CubicOut)
            );

            MinimizePanelButton.Text = "🔽";
            _panelVisible = true;
        }

        MinimizePanelButton.IsEnabled = true;
    }
    // 4. Add the handler for when a destination is broadcasted:
    private void OnDestinationSet(double destLat, double destLng, string destName)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _activeDestination = new Location(destLat, destLng);
            PendingDestinationLabel.Text = destName;
            PendingDestinationFrame.IsVisible = true;

            if (_amIAdmin)
            {
                StartJourneyButton.IsVisible = true;
                StartJourneyButton.IsEnabled = true;
            }
        });
    }
    // 5. Add the click handler for the Admin's "Start Journey" button:
    private async void OnStartJourneyClicked(object sender, EventArgs e)
    {
        StartJourneyButton.IsEnabled = false; // Prevent double taps
        string destName = PendingDestinationLabel.Text;

        // Now we officially start the navigation loop for the whole group!
        await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _activeDestination.Latitude, _activeDestination.Longitude, destName);
    }
    // 3. Add the event logic:
    private void OnUserJoined(string username)
    {
        _ = TextToSpeech.Default.SpeakAsync($"{username} has joined the group.");
    }

    private void OnUserLeft(string username)
    {
        _ = TextToSpeech.Default.SpeakAsync($"{username} has left the group.");
    }

    private async void OnGroupDeleted()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlert("Group Closed", "The Admin has deleted the group.", "OK");
            await Navigation.PopAsync();
        });
    }
    // --- LEAVE GROUP LOGIC ---
    private async void OnLeaveGroupClicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Leave Group", "Are you sure you want to permanently leave the group?", "Yes", "Cancel");
        if (confirm)
        {
            _isLeavingGroupPermanently = true; // Mark as permanent!
            await _signalRService.LeaveGroup();
            await Navigation.PopAsync();
        }
    }
    // --- OVERLAY BUTTON HANDLERS ---
    private void OnOpenSettingsClicked(object sender, EventArgs e)
    {
        // Opens the OS-level settings app so the user can flip the GPS/Permissions switch
        AppInfo.Current.ShowSettingsUI();
    }

    private void OnRetryLocationClicked(object sender, EventArgs e)
    {
        // Re-run the initialization logic. If GPS is now on, the overlay will disappear!
        InitializeLocalTrackingAsync();
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
     private async void OnRiderTapped(object sender, TappedEventArgs e)
    {
        // 1. Ensure a rider was passed from the CommandParameter
        if (e.Parameter is not Rider selectedRider) 
            return;

        // 2. Only Admins can assign roles.
        if (!_amIAdmin) 
        {
            // Optional: Give feedback that they don't have permission
            // await DisplayAlert("Access Denied", "Only the Admin can assign roles.", "OK");
            return;
        }

        // 3. Don't let the Admin change their own core role
        if (selectedRider.IsAdmin) 
        {
            await DisplayAlert("Role Assignment", "You cannot change your own Admin role.", "OK");
            return;
        }

        // 4. Pop up the Role Selection Menu
        string action = await DisplayActionSheet($"Assign role to {selectedRider.Name}", "Cancel", null, "Lead", "Tail", "Marshal", "Standard Rider");
        
        if (action != "Cancel" && !string.IsNullOrEmpty(action)) 
        {
            string backendRole = action == "Standard Rider" ? "Rider" : action;
            //Riders.FirstOrDefault(r => r.GoogleId == selectedRider.GoogleId)?.Role = backendRole;
            await _signalRService.AssignRole(GroupNameLabel.Text, selectedRider.GoogleId, backendRole);
        }
    }

    // 3. NEW: Group Size Slider Handler
    private void OnSizeSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        SizeSlider.Value = roundedValue;
        SizeValueLabel.Text = $"{roundedValue} Riders";
    }
    private void OnRecenterMapClicked(object sender, EventArgs e)
    {
        if (_lastKnownLocation != null)
        {
            // Instantly snap map back to user location with a tight zoom
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(_lastKnownLocation, Distance.FromMiles(0.5)));

            // Re-enable 3D auto-centering if it was broken by manual panning
            if (_myPinVm != null) _myPinVm.IsAutoCentering = true;

            // If we are in Overview mode, switch it back natively
            OverviewButton.IsVisible = true;
            ResumeNavButton.IsVisible = false;
        }
    }
}