#if ANDROID
using AndroidX.ConstraintLayout.Core.Motion.Utils;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SpeedyCompass.Controls;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models;
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
            return DestinationTemplate;
        }
        return RiderTemplate;
    }
}

public class TabClickedEventArgs : EventArgs { public bool FromNavigationStarted { get; set; } }

public partial class LobbyPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private GroupDetailsDto groupDetails;
    private readonly string _googleApiKey;
    private static readonly HttpClient _httpClient = new();

    private bool _hasJoined = false;
    private bool _amIAdmin = false;
    private string _myName = "";
    private DateTime _stateStartTime;
    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);

    // UI State Collections for XAML Binding
    public ObservableCollection<Rider> Riders { get; set; } = new();
    public ObservableCollection<RiderPin> MapPins { get; } = new ObservableCollection<RiderPin>();

    // Tracking State
    private bool _isTracking = false;
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

    private double _currentHeading = 0;
    private int _autocompleteApiHits = 0;
    private CancellationTokenSource _debounceCts;

    private bool _isLeavingGroupPermanently = false;
    private bool _isSelectingLocation = false;

    // --- PTT State ---
    private readonly HardwareButtonService _hwButtonService;
    private string _currentSpeaker = string.Empty;
    private CancellationTokenSource _pttCts;
    private int _pttTimeRemaining;

    // --- GOOGLE MAPS STYLE DRAWER STATE ---
    private double _drawerFullHeight;
    private double _drawerPeekHeight = 160;
    private double _currentDrawerTranslation = 0;
    private ILocationTracker? _locationTracker;
    // --- NEW: REROUTING VARIABLE ---
    private DateTime _lastRerouteTime = DateTime.MinValue;

    public LobbyPage(SignalRService signalRService, GroupDetailsDto groupDetails)
    {
        InitializeComponent();
        BindingContext = this;

        bool keepScreenOn = Preferences.Default.Get("KeepScreenOn", false);
        DeviceDisplay.Current.KeepScreenOn = keepScreenOn;

        _signalRService = signalRService;
        this.groupDetails = groupDetails;

#if ANDROID
        MainActivity.OnPiPModeChangedEvent += HandlePiPModeChanged;
#endif

#if ANDROID
        _locationTracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
        if (_locationTracker != null)
        {
            _locationTracker.LocationUpdated += OnLocalLocationPushedFromBackground;
        }
#endif

        var config = Application.Current?.MainPage?.Handler?.MauiContext?.Services?.GetService<IConfiguration>();
        _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk" ?? throw new Exception("API Key missing");

        GroupNameLabel.Text = this.groupDetails.GroupName;
        _myName = Preferences.Default.Get("username", "Unknown");
        _amIAdmin = this.groupDetails.AdminGoogleId == CurrentGoogleId;

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
        _signalRService.NavigationCancelled += OnNavigationCancelled;
        _signalRService.AlertReceived += OnAlertReceived;
        _signalRService.DestinationSet += OnDestinationSet;
        _signalRService.UserJoinedAlert += OnUserJoined;
        _signalRService.UserLeftAlert += OnUserLeft;
        _signalRService.GroupDeleted += OnGroupDeleted;
        _signalRService.PttLocked += OnPttLocked;
        _signalRService.PttDenied += OnPttDenied;
        _signalRService.PttReleased += OnPttReleased;
        _signalRService.NavigationPaused += OnNavigationPaused;
        _signalRService.NavigationResumed += OnNavigationResumed;
        _signalRService.NavigationCompleted += OnNavigationCompleted;

        _hwButtonService = IPlatformApplication.Current?.Services.GetService<HardwareButtonService>();
        if (_hwButtonService != null)
        {
            _hwButtonService.PttPressed += OnHardwarePttPressed;
            _hwButtonService.PttReleased += OnHardwarePttReleased;
        }
    }

    // --- DRAWER LIFECYCLE & PHYSICS ---
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (height > 0)
        {
            _drawerFullHeight = height * 0.85;
            ActionDrawer.HeightRequest = _drawerFullHeight;

            if (ActionDrawer.IsVisible && ActionDrawer.TranslationY == 0)
            {
                ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;
            }
        }
    }

    private void OnDrawerPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        double maxTranslation = _drawerFullHeight - _drawerPeekHeight;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _currentDrawerTranslation = ActionDrawer.TranslationY;
                break;

            case GestureStatus.Running:
                // Smoothly drag the drawer up and down with the finger
                double newTranslation = _currentDrawerTranslation + e.TotalY;
                // Clamp it so they can't drag it too high off screen or too low past the peek
                ActionDrawer.TranslationY = Math.Max(0, Math.Min(newTranslation, maxTranslation));
                break;

            case GestureStatus.Completed:
                // Snap physics: If dragged past halfway, snap to top. Otherwise, snap to peek.
                if (ActionDrawer.TranslationY < maxTranslation * 0.4)
                {
                    // Snap Full Screen
                    ActionDrawer.TranslateTo(0, 0, 250, Easing.CubicOut);
                }
                else
                {
                    // Snap to Peek at bottom
                    ActionDrawer.TranslateTo(0, maxTranslation, 250, Easing.CubicOut);
                }
                break;
        }
    }

    // --- NEW: DRAWER HANDLE CLICK LOGIC ---
    private void OnDrawerHandleTapped(object sender, TappedEventArgs e)
    {
        double maxTranslation = _drawerFullHeight - _drawerPeekHeight;

        // If the drawer is closer to the top (open), snap it down to the peek state
        if (ActionDrawer.TranslationY < maxTranslation * 0.5)
        {
            ActionDrawer.TranslateTo(0, maxTranslation, 250, Easing.CubicOut);
        }
        else // If it is currently peeked at the bottom, snap it fully open
        {
            ActionDrawer.TranslateTo(0, 0, 250, Easing.CubicOut);
        }
    }

    private void OnDrawerTabClicked(object sender, EventArgs e)
    {
        TabActionsBtn.BackgroundColor = Colors.Transparent;
        TabActionsBtn.TextColor = Colors.Gray;
        TabStatsBtn.BackgroundColor = Colors.Transparent;
        TabStatsBtn.TextColor = Colors.Gray;
        TabAdminBtn.BackgroundColor = Colors.Transparent;
        TabAdminBtn.TextColor = Colors.Gray;

        DrawerActionsTab.IsVisible = false;
        DrawerStatsTab.IsVisible = false;
        DrawerAdminTab.IsVisible = false;

        if (sender == TabActionsBtn)
        {
            TabActionsBtn.BackgroundColor = Colors.DodgerBlue;
            TabActionsBtn.TextColor = Colors.White;
            DrawerActionsTab.IsVisible = true;
        }
        else if (sender == TabStatsBtn)
        {
            TabStatsBtn.BackgroundColor = Colors.DodgerBlue;
            TabStatsBtn.TextColor = Colors.White;
            DrawerStatsTab.IsVisible = true;
            _ = RefreshTelemetryData();
        }
        else if (sender == TabAdminBtn)
        {
            TabAdminBtn.BackgroundColor = Colors.DodgerBlue;
            TabAdminBtn.TextColor = Colors.White;
            DrawerAdminTab.IsVisible = true;
        }

        double maxTranslation = _drawerFullHeight - _drawerPeekHeight;
        if (ActionDrawer.TranslationY >= maxTranslation - 10)
        {
            ActionDrawer.TranslateTo(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
        }
    }

    // --- PIP TOGGLE ---
    private void HandlePiPModeChanged(bool isPipMode)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (isPipMode)
            {
                TabRoster.IsVisible = false;
                TabMap.IsVisible = false;
                DestinationSearchBar.IsVisible = false;
                ActionDrawer.IsVisible = false;
                FloatingMapControls.IsVisible = false;
                MapView.IsVisible = false;

                PipRiderCountLabel.Text = $"{Riders.Count(r => r.IsOnline)}/{Riders.Count} Riders";
                PipSpeedLabel.Text = _myPinVm?.Speed ?? "0 mph";
                PipOverlayGrid.IsVisible = true;
            }
            else
            {
                PipOverlayGrid.IsVisible = false;
                TabRoster.IsVisible = true;
                TabMap.IsVisible = true;
                DestinationSearchBar.IsVisible = true;
                MapView.IsVisible = true;

                if (groupDetails?.CurrentState == GroupState.Navigating ||
                    groupDetails?.CurrentState == GroupState.PausedBreak ||
                    groupDetails?.CurrentState == GroupState.PausedHazard ||
                    groupDetails?.CurrentState == GroupState.PausedMechanical)
                {
                    ActionDrawer.IsVisible = true;
                    FloatingMapControls.IsVisible = true;
                }
            }
        });
    }

    // --- STATE MACHINE ---
    private async Task ChangeGroupState(GroupState newState, string triggerUser = "", string reason = "")
    {
        if (this.groupDetails.CurrentState == newState) return;

        this.groupDetails.CurrentState = newState;
        _stateStartTime = DateTime.Now;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (newState)
            {
                case GroupState.NotNavigating:
                case GroupState.Completed:
                    ActionDrawer.IsVisible = false;
                    FloatingMapControls.IsVisible = false;
                    PendingDestinationFrame.IsVisible = false;
                    ConfirmDestButton.IsVisible = true;
                    ResetDestButton.IsVisible = false;
                    DestinationSearchBar.IsReadOnly = false;
                    DestinationSearchBar.Text = string.Empty;

                    _locationTracker?.StopTracking();
                    _isSimulating = false;

                    if (_activeRouteLine != null)
                    {
                        LiveMap.MapElements.Remove(_activeRouteLine);
                        _activeRouteLine = null;
                    }
                    var oldDest = LiveMap.Pins.FirstOrDefault(p => p.Label != "You" && p.Type == PinType.Place);
                    if (oldDest != null) LiveMap.Pins.Remove(oldDest);

                    FitMapToBounds();

#if ANDROID
                    MainActivity.IsInNavigationMode = false;
#endif
                    if (newState == GroupState.Completed)
                        _ = TextToSpeech.Default.SpeakAsync($"Navigation completed by {triggerUser}. Great ride!");
                    break;

                case GroupState.Navigating:
                    OnTabClicked(TabMap, new TabClickedEventArgs() { FromNavigationStarted = true });
                    PendingDestinationFrame.IsVisible = false;
                    AdminInstructionBanner.IsVisible = false;
                    DestinationSearchBar.IsReadOnly = true;
                    ConfirmDestButton.IsVisible = false;
                    ResetDestButton.IsVisible = true;

                    ActionDrawer.IsVisible = true;
                    FloatingMapControls.IsVisible = true;
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;

                    // Admin Visibility Rules
                    TabAdminBtn.IsVisible = _amIAdmin;
                    if (_amIAdmin)
                    {
                        PauseNavBtn.IsVisible = true;
                        ResumeNavBtn.IsVisible = false;
                        CompleteNavBtn.IsVisible = true;
                    }

                    SetActionButtonsEnabled(true);
                    _locationTracker?.StartTracking(GroupNameLabel.Text, Riders.Count(x => x.IsOnline));

#if ANDROID
                    MainActivity.IsInNavigationMode = true;
#endif
                    if (string.IsNullOrEmpty(triggerUser))
                        _ = TextToSpeech.Default.SpeakAsync("Navigation active. Ride safe!");
                    break;

                case GroupState.PausedBreak:
                case GroupState.PausedHazard:
                case GroupState.PausedMechanical:
                    _locationTracker?.StopTracking();
                    SetActionButtonsEnabled(false);

                    if (_amIAdmin)
                    {
                        PauseNavBtn.IsVisible = false;
                        ResumeNavBtn.IsVisible = true;
                        CompleteNavBtn.IsVisible = true;
                    }

                    string context = newState == GroupState.PausedBreak ? "for a break" :
                                     newState == GroupState.PausedHazard ? "due to a hazard" :
                                     "for mechanical repairs";

                    string spokenReason = string.IsNullOrEmpty(reason) ? context : reason;
                    _ = TextToSpeech.Default.SpeakAsync($"Navigation paused by {triggerUser} {spokenReason}. Tracking suspended.");

                    // Force open the Telemetry Dashboard tab
                    ActionDrawer.TranslateTo(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
                    OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty);

                    break;
            }
        });
    }

    private void SetActionButtonsEnabled(bool isEnabled)
    {
        DrawerActionsTab.IsEnabled = isEnabled;
        DrawerActionsTab.Opacity = isEnabled ? 1.0 : 0.4;
    }

    // --- PTT TIMEOUT & TRIGGERS ---
    private async Task RunPttTimeoutAsync(CancellationToken token)
    {
        _pttTimeRemaining = 30;
        try
        {
            while (_pttTimeRemaining > 0 && !token.IsCancellationRequested)
            {
                MainThread.BeginInvokeOnMainThread(() => PttCountdownLabel.Text = $"Auto-closing in {_pttTimeRemaining}s...");
                await Task.Delay(1000, token);
                _pttTimeRemaining--;
            }

            if (_pttTimeRemaining <= 0 && !token.IsCancellationRequested)
            {
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
        catch (TaskCanceledException) { }
    }

    private async void OnHardwarePttPressed(object sender, EventArgs e)
    {
        if (_currentSpeaker == _myName) return;
        await _signalRService.RequestPtt(GroupNameLabel.Text, _myName);
    }

    private async void OnHardwarePttReleased(object sender, EventArgs e)
    {
        if (_currentSpeaker == _myName)
        {
            await _signalRService.ReleasePtt(GroupNameLabel.Text, _myName);
        }
    }

    private void OnPttLocked(string speakerName)
    {
        _currentSpeaker = speakerName;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PttOverlay.IsVisible = true;
            if (speakerName == _myName)
            {
                PttStatusLabel.Text = "MIC OPEN";
                PttStatusLabel.TextColor = Colors.MediumSeaGreen;
                PttSpeakerLabel.Text = "You can now speak to the group.";

                _pttCts?.Cancel();
                _pttCts = new CancellationTokenSource();
                PttCountdownLabel.IsVisible = true;
                _ = RunPttTimeoutAsync(_pttCts.Token);

                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));
                _ = TextToSpeech.Default.SpeakAsync("You can now speak.");
            }
            else
            {
                _pttCts?.Cancel();
                PttCountdownLabel.IsVisible = false;
                PttStatusLabel.Text = "RECEIVING";
                PttStatusLabel.TextColor = Colors.DodgerBlue;
                PttSpeakerLabel.Text = $"{speakerName} is speaking...";
            }
        });
    }

    private void OnPttDenied(string activeSpeaker)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _ = TextToSpeech.Default.SpeakAsync("Channel busy.");
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
        });
    }

    private void OnPttReleased()
    {
        _currentSpeaker = string.Empty;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _pttCts?.Cancel();
            PttOverlay.IsVisible = false;
            PttCountdownLabel.IsVisible = false;
        });
    }

    // --- BACKGROUND LOCATION OVERRIDES ---
    private async void OnLocalLocationPushedFromBackground(object sender, LocalLocationUpdate e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            LocationDisabledOverlay.IsVisible = false;
            if (_myPinVm != null)
            {
                _myPinVm.Location = e.Location;
                _myPinVm.Speed = $"{Math.Round(e.SpeedMph)} kmph";
                _myPinVm.Heading = e.Heading;
            }
        });

        // --- THE FIX: Trim the blue line dynamically! ---
        if (groupDetails?.CurrentState == GroupState.Navigating)
        {
            //await TrimRouteVisuals(e.Location);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_hasJoined) return;

        try
        {
            OnConnectionStatusChanged("Connected", Colors.MediumSeaGreen);

            var roster = await _signalRService.GetGroupRoster(GroupNameLabel.Text);
            if (roster != null)
            {
                OnRosterUpdated(roster);
            }

            _hasJoined = true;
            AdminSettingsBtn.IsVisible = _amIAdmin;
            await InitializeLocalTrackingAsync();

            if (groupDetails != null)
            {
                if (groupDetails.CurrentState == GroupState.DestinationSet)
                {
                    OnDestinationSet(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                    _isSelectingLocation = true;
                    DestinationSearchBar.Text = groupDetails.DestName;
                    AdminInstructionBanner.IsVisible = false;
                    ConfirmDestButton.IsVisible = false;
                    ResetDestButton.IsVisible = true;
                    _isSelectingLocation = false;
                }

                if (groupDetails.CurrentState == GroupState.Navigating)
                {
                    groupDetails.CurrentState = GroupState.NotNavigating;
                    OnNavigationStarted(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName, true);
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not load lobby: {ex.Message}", "OK");
            await Navigation.PopAsync();
        }
    }

    // --- ADMIN SETTINGS ---
    private async void OnAdminSettingsClicked(object sender, EventArgs e)
    {
        var currentSettings = await _signalRService.GetGroupSettings(GroupNameLabel.Text);
        if (currentSettings != null)
        {
            LagSlider.Value = currentSettings.MaxLagDistanceMeters;
            SplinterSlider.Value = currentSettings.SplinterWarningDistanceMeters;
            SizeSlider.Value = currentSettings.MaxGroupSize;
            PitstopSlider.Value = currentSettings.PitstopDistanceMeters / 1000;
        }
        AdminSettingsOverlay.IsVisible = true;
    }

    private void OnCloseSettingsClicked(object sender, EventArgs e) => AdminSettingsOverlay.IsVisible = false;
    private void OnLagSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 50.0) * 50;
        LagSlider.Value = roundedValue;
        LagValueLabel.Text = $"{roundedValue}m";
    }
    private void OnSplinterSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 100.0) * 100;
        SplinterSlider.Value = roundedValue;
        SplinterValueLabel.Text = $"{roundedValue}m";
    }
    private async void OnSaveSettingsClicked(object sender, EventArgs e)
    {
        int maxLag = (int)LagSlider.Value;
        int splinterDist = (int)SplinterSlider.Value;
        int maxSize = (int)SizeSlider.Value;
        int pitstopDistMeters = (int)PitstopSlider.Value * 1000;

        await _signalRService.UpdateGroupSettings(GroupNameLabel.Text, maxLag, splinterDist, maxSize, pitstopDistMeters);
        AdminSettingsOverlay.IsVisible = false;
        Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100));
    }

    private async void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            StatusLabel.TextColor = color;
            StatusDot.BackgroundColor = color;
            MapTabStatusDot.Fill = color;
            if (StatusLabel.Parent is View parentView)
            {
                parentView.InvalidateMeasure();
            }
        });
        if (color == Colors.MediumSeaGreen)
        {
            var fetchedDetails = await _signalRService.GetGroupDetails(GroupNameLabel.Text);
            if (fetchedDetails != null)
            {
                this.groupDetails = fetchedDetails;
            }
        }
    }

    private async void OnTabClicked(object sender, EventArgs e)
    {
        bool calledFromNavStart = e is TabClickedEventArgs tce && tce.FromNavigationStarted;

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

            if (!calledFromNavStart && _activeDestination != null)
            {
                var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
                UpdateDestinationPin(_activeDestination, DestinationSearchBar.Text ?? PendingDestinationLabel.Text ?? "Selected Destination");
                await CalculateAndDrawRoute(currentLoc, _activeDestination);

                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _activeDestination]));
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());
            }
        }
    }

    // --- SEARCH AND MAP ACTIONS ---
    private async void OnMapClicked(object sender, MapClickedEventArgs e)
    {
        if (!_amIAdmin || groupDetails?.CurrentState == GroupState.Navigating) return;

        _pendingDestination = e.Location;
        UpdateDestinationPin(_pendingDestination, "Selected Destination");
        var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
        await CalculateAndDrawRoute(currentLoc, _pendingDestination);
        MainThread.BeginInvokeOnMainThread(async () => FitMapToBounds([currentLoc, _pendingDestination]));

        try
        {
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
        var oldDest = LiveMap.Pins.FirstOrDefault(p => p.Label != "You" && p.Type == PinType.Place);
        if (oldDest != null) LiveMap.Pins.Remove(oldDest);

        LiveMap.Pins.Add(new Pin() { Label = label, Location = location, Type = PinType.Place });
    }

    private async void OnConfirmDestinationClicked(object sender, EventArgs e)
    {
        if (_pendingDestination == null || _lastKnownLocation == null) return;

        ConfirmDestButton.IsVisible = false;
        ResetDestButton.IsVisible = true;
        DestinationSearchBar.IsReadOnly = true;
        AdminInstructionBanner.IsVisible = false;

        string destName = DestinationSearchBar.Text ?? "Destination";
        _activeDestination = _pendingDestination;

        groupDetails.CurrentState = GroupState.DestinationSet;

        OnTabClicked(TabRoster, EventArgs.Empty);

        await _signalRService.SetGroupDestination(GroupNameLabel.Text, _pendingDestination.Latitude, _pendingDestination.Longitude, destName);
    }

    private async void OnResetDestinationClicked(object sender, EventArgs e)
    {
        if (groupDetails != null)
        {
            //If not navigating but destination was set
            PendingDestinationFrame.IsVisible = false;
            ConfirmDestButton.IsVisible = true;
            ResetDestButton.IsVisible = false;
            DestinationSearchBar.IsReadOnly = false;
            DestinationSearchBar.Text = string.Empty;
            _activeDestination = null;
        }
        await ChangeGroupState(GroupState.NotNavigating);
        await _signalRService.CancelGroupNavigation(GroupNameLabel.Text);
    }

    // --- HUB EVENT RESPONDERS ---
    private async void OnNavigationStarted(double destLat, double destLng, string destName, bool isSyncRequired = false)
    {
        _activeDestination = new Location(destLat, destLng);
        DestinationSearchBar.Text = destName;
        await ChangeGroupState(GroupState.Navigating, _myName);
        Location loc;

#if DEBUG
        loc = _lastKnownLocation ?? await Geolocation.Default.GetLastKnownLocationAsync();
#else
        loc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
#endif

        if (loc != null)
        {
            await CalculateAndDrawRoute(loc, _activeDestination);
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());

#if DEBUG
            if (_currentRoutePoints != null && _currentRoutePoints.Any() && !_isSimulating)
            {
                _ = SimulateMovementAlongRouteAsync();
            }
#endif
        }
        if (!isSyncRequired)
        {
            _ = TextToSpeech.Default.SpeakAsync($"Navigation started to {destName}. Ride safe!");
        }
    }

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
                _currentRoutePoints = DecodeGooglePolyline(mainRoute.Polyline.EncodedPolyline);
                if (_currentRoutePoints == null || _currentRoutePoints.Count == 0) return;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        var oldLines = LiveMap.MapElements.OfType<Polyline>().ToList();
                        foreach (var line in oldLines)
                        {
                            LiveMap.MapElements.Remove(line);
                        }

                        _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 8 };
                        foreach (var coord in _currentRoutePoints)
                        {
                            _activeRouteLine.Geopath.Add(coord);
                        }
                        LiveMap.MapElements.Add(_activeRouteLine);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"UI Map update error: {ex.Message}");
                    }
                });
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
    private async Task InitializeLocalTrackingAsync()
    {
        ShowLoading("Initializing...");
        try
        {
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

            var micStatus = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (micStatus != PermissionStatus.Granted)
            {
                await Permissions.RequestAsync<Permissions.Microphone>();
            }

            if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Version.Major >= 13)
            {
                var notifStatus = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                if (notifStatus != PermissionStatus.Granted)
                {
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
                }
            }

#if ANDROID
            // --- NEW: BATTERY OPTIMIZATION OVERRIDE ---
            // Ask Android to never kill our SignalR connection or GPS tracker when the screen is locked!
            var pm = (global::Android.OS.PowerManager)global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.PowerService);
            if (!pm.IsIgnoringBatteryOptimizations(global::Android.App.Application.Context.PackageName))
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    bool accept = await DisplayAlert("Background Tracking", "To keep navigation active while your screen is locked, please allow unrestricted background activity on the next screen.", "OK", "Cancel");
                    if (accept)
                    {
                        var intent = new global::Android.Content.Intent();
                        intent.SetAction(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                        intent.SetData(global::Android.Net.Uri.Parse("package:" + global::Android.App.Application.Context.PackageName));
                        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                        global::Android.App.Application.Context.StartActivity(intent);
                    }
                });
            }
#endif

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
        finally
        {
            HideLoading();
        }
    }

    private void OnRiderLocationUpdated(string riderId, double lat, double lng, double heading)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var newLoc = new Location(lat, lng);

            if (_riderViewModels.TryGetValue(riderId, out var existingVm))
            {
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
                    ZIndex = 50F
                };

                _riderViewModels.TryAdd(riderId, newVm);
                MapPins.Add(newVm);
            }
        });
    }

    private void FitMapToBounds()
    {
        if (MapPins.Count == 0) return;

        double minLat = double.MaxValue, minLng = double.MaxValue;
        double maxLat = double.MinValue, maxLng = double.MinValue;

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
        var updatedRiders = new ObservableCollection<Rider>();
        string myName = Preferences.Default.Get("username", "Rider");

        foreach (var r in roster)
        {
            string displayName = r.Name;

            if (displayName == myName)
            {
                displayName += " (You)";
                _amIAdmin = r.IsAdmin;
            }

            if (!r.IsOnline)
                displayName += " (Offline)";

            updatedRiders.Add(new Rider
            {
                Name = displayName,
                GoogleId = r.GoogleId,
                IsAdmin = r.IsAdmin,
                Role = r.Role,
                IsOnline = r.IsOnline
            });
        }

        Riders = updatedRiders;
        RidersCollectionView.ItemsSource = Riders;

        PipRiderCountLabel.Text = $"{Riders.Count(r => r.IsOnline)}/{Riders.Count} Riders";
            
        if (_locationTracker != null)
        {
            _locationTracker.UpdateRiderCount(Riders.Count(r => r.IsOnline));
        }

        });
    }

    protected async override void OnDisappearing()
    {
        base.OnDisappearing();

        _signalRService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _signalRService.RosterUpdated -= OnRosterUpdated;
        _signalRService.NavigationStarted -= OnNavigationStarted;
        _signalRService.RiderLocationUpdated -= OnRiderLocationUpdated;
        _signalRService.NavigationCancelled -= OnNavigationCancelled;
        _signalRService.AlertReceived -= OnAlertReceived;
        _signalRService.DestinationSet -= OnDestinationSet;
        _signalRService.UserJoinedAlert -= OnUserJoined;
        _signalRService.UserLeftAlert -= OnUserLeft;
        _signalRService.GroupDeleted -= OnGroupDeleted;
        _signalRService.PttLocked -= OnPttLocked;
        _signalRService.PttDenied -= OnPttDenied;
        _signalRService.PttReleased -= OnPttReleased;
        _signalRService.NavigationPaused -= OnNavigationPaused;
        _signalRService.NavigationResumed -= OnNavigationResumed;
        _signalRService.NavigationCompleted -= OnNavigationCompleted;

        if (_hwButtonService != null)
        {
            _hwButtonService.PttPressed -= OnHardwarePttPressed;
            _hwButtonService.PttReleased -= OnHardwarePttReleased;
        }

        _isTracking = false;
        _isSimulating = false;
        _locationTracker?.StopTracking();

#if ANDROID
        MainActivity.OnPiPModeChangedEvent -= HandlePiPModeChanged;
        MainActivity.IsInNavigationMode = false;
#endif

        if (!_isLeavingGroupPermanently)
        {
            _ = _signalRService.LeaveLobby();
        }
        await _signalRService.StopAsync();
    }

    private void MapPinClicked(RiderPin pin)
    {
        // Handle pin click
    }

    private async Task SimulateMovementAlongRouteAsync()
    {
        if (_currentRoutePoints == null || _currentRoutePoints.Count == 0) return;

        await Task.Delay(2000);
        _isSimulating = true;

        if (_locationTracker != null) _locationTracker.IsSimulating = true;

        foreach (var point in _currentRoutePoints.ToList())
        {
            if (groupDetails.CurrentState != GroupState.Navigating || !_isSimulating)
            {
                _isSimulating = false;
                break;
            }

            double fakeHeading = _lastKnownLocation != null
                ? CalculateBearing(_lastKnownLocation, point)
                : 0;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_myPinVm != null)
                {
                    _myPinVm.Location = point;
                    _myPinVm.Speed = "Simulated";
                    _myPinVm.Heading = fakeHeading;
                }
            });

            

            _lastKnownLocation = point;
            await _signalRService.UpdateLocation(GroupNameLabel.Text, _myName, point.Latitude, point.Longitude, fakeHeading);

            //await TrimRouteVisuals(point);

            await Task.Delay(2000);
        }

        _isSimulating = false;
    }

    private async void OnNavigationCancelled()
    {
#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif

        _locationTracker?.StopTracking();

        await ChangeGroupState(GroupState.NotNavigating);
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (groupDetails?.CurrentState == GroupState.Navigating) return;
        if (_isSelectingLocation) return;
        if (e.OldTextValue == e.NewTextValue) return;

        string query = e.NewTextValue;

        if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
        {
            SuggestionsFrame.IsVisible = false;
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();

        try
        {
            _autocompleteApiHits++;

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
                var displayList = result.Suggestions
                    .Where(s => s.PlacePrediction != null)
                    .Select(s => new UIPlaceSuggestion
                    {
                        Description = s.PlacePrediction.Text.Text,
                        PlaceId = s.PlacePrediction.PlaceId
                    }).ToList();

                SuggestionsListView.ItemsSource = displayList;
                SuggestionsFrame.IsVisible = true;
            }

            await Task.Delay(1000, _debounceCts.Token);

            if (_autocompleteApiHits != _autocompleteApiHits)
                return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
        }
    }

    private async void OnSuggestionSelected(object sender, SelectedItemChangedEventArgs e)
    {
        if (e.SelectedItem is UIPlaceSuggestion selectedPlace)
        {
            SuggestionsFrame.IsVisible = false;
            _isSelectingLocation = true;
            DestinationSearchBar.Text = selectedPlace.Description;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://places.googleapis.com/v1/places/{selectedPlace.PlaceId}");
                request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
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

                    LiveMap.Pins.Clear();

                    var pin = new Pin
                    {
                        Label = selectedPlace.Description,
                        Type = PinType.Place,
                        Location = _pendingDestination
                    };
                    LiveMap.Pins.Add(pin);

                    var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
                    await CalculateAndDrawRoute(currentLoc, _pendingDestination);
                    FitMapToBounds([currentLoc, _pendingDestination]);

                    ConfirmDestButton.IsEnabled = true;
                    ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Could not fetch location details.", "OK");
                System.Diagnostics.Debug.WriteLine($"Details Error: {ex.Message}");
            }

            SuggestionsListView.SelectedItem = null;
            _isSelectingLocation = false;
        }
    }

    private void FitMapToBounds(List<Location> points)
    {
        if (points == null || !points.Any()) return;

        if (points.Count == 1)
        {
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(points.First(), Distance.FromKilometers(1)));
            return;
        }

        double minLat = double.MaxValue, minLng = double.MaxValue;
        double maxLat = double.MinValue, maxLng = double.MinValue;

        foreach (var loc in points)
        {
            if (loc.Latitude < minLat) minLat = loc.Latitude;
            if (loc.Latitude > maxLat) maxLat = loc.Latitude;
            if (loc.Longitude < minLng) minLng = loc.Longitude;
            if (loc.Longitude > maxLng) maxLng = loc.Longitude;
        }

        double centerLat = (minLat + maxLat) / 2.0;
        double centerLng = (minLng + maxLng) / 2.0;

        double latDistance = Math.Max(0.01, (maxLat - minLat) * 1.5);
        double lngDistance = Math.Max(0.01, (maxLng - minLng) * 1.5);

        LiveMap.MoveToRegion(new MapSpan(new Location(centerLat, centerLng), latDistance, lngDistance));
    }

    // --- REPLACED MAP CAMERA MODES ---
    private void OnRecenterMapClicked(object sender, EventArgs e)
    {
        if (_lastKnownLocation != null)
        {
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(_lastKnownLocation, Distance.FromMiles(0.5)));
            if (_myPinVm != null) _myPinVm.IsAutoCentering = true;

            OverviewButton.IsVisible = true;
            MapFollowButton.IsVisible = false;
        }
    }

    private async void OnOverviewClicked(object sender, EventArgs e)
    {
        if (_myPinVm == null) return;

        OverviewButton.IsVisible = false;
        MapFollowButton.IsVisible = true;

        _myPinVm.IsAutoCentering = false;

        await LiveMap.RotateTo(0, 500, Microsoft.Maui.Easing.SinInOut);
        LiveMap.Scale = 1.0;

        FitMapToBounds();
    }

    private async void OnEmergencyStopClicked(object sender, EventArgs e)
    {
        DrawerActionsTab.IsEnabled = false;
        await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Emergency", _myName);
        DrawerActionsTab.IsEnabled = true;
    }

    private async void OnRefuelStopClicked(object sender, EventArgs e)
    {
        DrawerActionsTab.IsEnabled = false;
        await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Refuel", _myName);
        DrawerActionsTab.IsEnabled = true;
    }

    private async void OnRestStopClicked(object sender, EventArgs e)
    {
        DrawerActionsTab.IsEnabled = false;
        await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Rest", _myName);
        DrawerActionsTab.IsEnabled = true;
    }

    private async void OnAlertReceived(string alertType, string senderName)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (alertType == "Lagging" || alertType == "Splinter" || alertType == "VoicePrompt")
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));
                _ = TextToSpeech.Default.SpeakAsync(senderName);
                return;
            }
            int durationSeconds = 5;
            string voiceMessage = "";

            SetActionButtonsEnabled(false);

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

            _ = TextToSpeech.Default.SpeakAsync(voiceMessage);

            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
                    await SensoryAlertOverlay.FadeToAsync(0.8, 250);
                    await SensoryAlertOverlay.FadeToAsync(0.2, 250);
                }
            }
            catch (TaskCanceledException) { }

            Vibration.Default.Cancel();
            SensoryAlertOverlay.IsVisible = false;
            SensoryAlertOverlay.Opacity = 0;

            SetActionButtonsEnabled(true);
        });
    }

    private async void OnStartJourneyClicked(object sender, EventArgs e)
    {
        ShowLoading("Starting Navigation...");
        try
        {
            StartJourneyButton.IsEnabled = false;
            string destName = PendingDestinationLabel.Text;

            await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _activeDestination.Latitude, _activeDestination.Longitude, destName);
        }
        finally
        {
            HideLoading();
        }
    }

    private void OnUserJoined(string username) => _ = TextToSpeech.Default.SpeakAsync($"{username} has joined the group.");
    private void OnUserLeft(string username) => _ = TextToSpeech.Default.SpeakAsync($"{username} has left the group.");
    private async void OnGroupDeleted()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlert("Group Closed", "The Admin has deleted the group.", "OK");
            await Navigation.PopAsync();
        });
    }

    private async void OnLeaveGroupClicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Leave Group", "Are you sure you want to permanently leave the group?", "Yes", "Cancel");
        if (confirm)
        {
            _isLeavingGroupPermanently = true;
            await _signalRService.LeaveGroup(CurrentGoogleId);
            await Navigation.PopAsync();
        }
    }

    private void OnOpenSettingsClicked(object sender, EventArgs e) => AppInfo.Current.ShowSettingsUI();
    private void OnRetryLocationClicked(object sender, EventArgs e) => InitializeLocalTrackingAsync();

    private async void OnRiderTapped(object sender, TappedEventArgs e)
    {
        if (!_amIAdmin)
        {
            await DisplayAlert("Permission Denied", "Only the Admin can assign roles or view emergency info.", "OK");
            return;
        }

        if (e.Parameter is not Rider selectedRider)
        {
            await DisplayAlert("Error", "Could not identify the selected rider from the UI.", "OK");
            return;
        }

        string action = await DisplayActionSheet($"Manage {selectedRider.Name}", "Cancel", null,
            "View Emergency Info", "Lead", "Tail", "Marshal", "Standard Rider");

        if (action == "View Emergency Info")
        {
            try
            {
                var emergencyData = await _signalRService.GetRiderEmergencyInfo(selectedRider.GoogleId);
                if (emergencyData != null)
                {
                    string info = $"Blood Group: {emergencyData.BloodGroup}\n" +
                                  $"Contact: {emergencyData.EmergencyContact}\n" +
                                  $"Vehicle: {emergencyData.VehicleNumber}";

                    await DisplayAlert($"{selectedRider.Name}'s Info", info, "Close");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Access Denied", ex.Message, "OK");
            }
        }
        else if (action != "Cancel" && !string.IsNullOrEmpty(action))
        {
            string backendRole = action == "Standard Rider" ? "Rider" : action;

            if (selectedRider.IsAdmin && backendRole != "Lead")
            {
                bool hasOtherLead = Riders.Any(r => r.GoogleId != selectedRider.GoogleId && r.Role == "Lead");
                if (!hasOtherLead)
                {
                    await DisplayAlert("Action Denied", "At least another rider should be the Lead before you reassign yourself.", "OK");
                    return;
                }
            }

            await _signalRService.AssignRole(GroupNameLabel.Text, selectedRider.GoogleId, backendRole);
        }
    }

    private async Task RefreshTelemetryData()
    {
        var data = await _signalRService.GetGroupTelemetry(GroupNameLabel.Text);
        MainThread.BeginInvokeOnMainThread(() => TelemetryCollectionView.ItemsSource = data);
    }

    private void ShowLoading(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadingText.Text = message;
            LoadingOverlay.IsVisible = true;
        });
    }

    private void HideLoading()
    {
        MainThread.BeginInvokeOnMainThread(() => LoadingOverlay.IsVisible = false);
    }

    private async void OnPauseNavClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        string reason = await DisplayActionSheet("Reason for Pause?", "Cancel", null,
            "Fuel Stop", "Food/Rest Break", "Scenic Viewpoint", "Mechanical Issue", "Wait for Stragglers");

        if (reason == "Cancel" || string.IsNullOrEmpty(reason)) return;

        ShowLoading("Pausing Route...");
        try
        {
            await _signalRService.PauseGroupNavigation(GroupNameLabel.Text, reason, _myName);
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnResumeJourneyClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        ShowLoading("Resuming...");
        try
        {
            await _signalRService.ResumeGroupNavigation(GroupNameLabel.Text, _myName);
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnCompleteNavClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        bool confirm = await DisplayAlert("Complete Route", "Are you sure you want to end this journey? This will stop navigation for everyone.", "Finish Ride", "Cancel");
        if (!confirm) return;

        ShowLoading("Completing Route...");
        try
        {
            await _signalRService.CompleteGroupNavigation(GroupNameLabel.Text, _myName);
        }
        finally
        {
            HideLoading();
        }
    }

    private async void OnNavigationPaused(string reason, string adminName)
    {
        GroupState pauseState = GroupStateHelper.GetBreakState(reason);
        await ChangeGroupState(pauseState, adminName, reason);
    }

    private async void OnNavigationResumed(string adminName)
    {
        await ChangeGroupState(GroupState.Navigating, adminName);
        Location loc;

#if DEBUG
        loc = _lastKnownLocation ?? await Geolocation.Default.GetLastKnownLocationAsync();
#else
        loc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
#endif

        if (loc != null)
        {
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());

#if DEBUG
            if (_currentRoutePoints != null && _currentRoutePoints.Any() && !_isSimulating)
            {
                _ = SimulateMovementAlongRouteAsync();
            }
#endif
        }

            _ = TextToSpeech.Default.SpeakAsync($"Resuming Navigation to {groupDetails.DestName}. Ride safe!");
    }

    private async void OnNavigationCompleted(string adminName)
    {
        await ChangeGroupState(GroupState.Completed, adminName);
    }
    private async void OnDestinationSet(double destLat, double destLng, string destName)
    {
        ShowLoading("Drawing Route Preview..."); // LOCK UI
        try
        {
            _activeDestination = new Location(destLat, destLng);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                PendingDestinationLabel.Text = destName;
                PendingDestinationFrame.IsVisible = true;

                if (_amIAdmin)
                {
                    StartJourneyButton.IsVisible = true;
                    StartJourneyButton.IsEnabled = true;
                }
            });
        }
        finally
        {
            HideLoading();
        }
    }
    // 3. NEW: Group Size Slider Handler
    private void OnSizeSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        SizeSlider.Value = roundedValue;
        SizeValueLabel.Text = $"{roundedValue} Riders";
    }
    private async void OnRefreshTelemetryClicked(object sender, EventArgs e)
    {
        await RefreshTelemetryData();
    }
    private void OnPitstopSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        PitstopSlider.Value = roundedValue;
        PitstopValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue} km";
    }
    private void OnMapFollowClicked(object sender, EventArgs e)
    {
        if (_myPinVm == null) return;

        // 1. Swap Buttons
        MapFollowButton.IsVisible = false;
        OverviewButton.IsVisible = true; // Show the "Overview" toggle

        // 2. Tell the Android Handler to take control again!
        // As soon as this is true, the very next GPS tick will automatically 
        // swoop the camera back down into the 3D navigation view.
        _myPinVm.IsAutoCentering = true;
    }
    // =====================================================================
    // --- NEW: DYNAMIC ROUTE TRIMMER & AUTO-REROUTER ---
    // =====================================================================
    private async Task TrimRouteVisuals(Location currentLocation)
    {
        try
        {
            if (_activeRouteLine == null || _activeRouteLine.Geopath.Count < 2 || _activeDestination == null) return;

            double minDistance = double.MaxValue;
            int closestIndex = 0;

            // Search only the next 20 points ahead to avoid snapping to a return-loop later in the ride
            int searchRange = Math.Min(20, _currentRoutePoints.Count);
            for (int i = 0; i < searchRange; i++)
            {
                double dist = Location.CalculateDistance(currentLocation, _currentRoutePoints[i], DistanceUnits.Kilometers);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    closestIndex = i;
                }
            }

            // Check 1: Did they go WAY off route? (> 100 meters away from the line)
            if (minDistance > 0.1)
            {
                // Throttle the Google API calls! Only recalculate a full new route once every 15 seconds max.
                if ((DateTime.Now - _lastRerouteTime).TotalSeconds > 15)
                {
                    _lastRerouteTime = DateTime.Now;
                    await CalculateAndDrawRoute(currentLocation, _activeDestination);
                }
                return;
            }

            // Check 2: They are still on the line! Let's slice it perfectly.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    // Remove the coordinates that we have already physically passed
                    for (int i = 0; i < closestIndex; i++)
                    {
                        if (_activeRouteLine.Geopath.Count > 0) _activeRouteLine.Geopath.RemoveAt(0);
                        if (_currentRoutePoints.Count > 0) _currentRoutePoints.RemoveAt(0);
                    }

                    // Snap the very beginning of the polyline directly to the bike's front tire!
                    if (_activeRouteLine.Geopath.Count > 0)
                    {
                        _activeRouteLine.Geopath[0] = currentLocation;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Line Trimming Error: {ex.Message}");
                }
            });
        }
        catch(Exception ex)
        {

        }
    }
}