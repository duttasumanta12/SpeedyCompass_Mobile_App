#if ANDROID
using Android.Hardware;
using AndroidX.ConstraintLayout.Core.Motion.Utils;
using Kotlin.Contracts;

#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SpeedyCompass.Controls;
using SpeedyCompass.Engines;
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
// MVVM Model for the Map Pins


public class TabClickedEventArgs : EventArgs { public bool FromNavigationStarted { get; set; } }

public partial class LobbyPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly RideStateService _rideCache;
    private GroupDetailsDto groupDetails;
    private readonly IPlaceDiscoveryService _placeDiscoveryService;

    private bool _hasJoined = false;
    private bool _amIAdmin = false;
    private string _myName = "";
    private DateTime _stateStartTime;
    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);

    public ObservableCollection<Rider> Riders { get; set; } = new();
    public ObservableCollection<RiderPin> MapPins { get; } = new ObservableCollection<RiderPin>();

    private Location _lastKnownLocation; // Used primarily for UI/Map snapping
    private RiderPin _myPinVm;
    private Location _pendingDestination;
    private Polyline _activeRouteLine;

    private readonly ConcurrentDictionary<string, RiderPin> _riderViewModels = new();

    private bool _isSimulating = false;
    private bool _haveIReachedMeetup = false;
    private bool _hasAnnouncedArrival = false;
    private HashSet<string> _ridersAtMeetup = new();

    private bool _isLeavingGroupPermanently = false;

    // --- PTT State ---
    private readonly HardwareButtonService _hwButtonService;
    private string _currentSpeaker = string.Empty;
    private CancellationTokenSource _pttCts;
    private int _pttTimeRemaining;

    // --- DRAWER STATE ---
    private double _drawerFullHeight;
    private double _drawerPeekHeight = 180;
    private double _currentDrawerTranslation = 0;
    private ILocationTracker? _locationTracker;

    public string ConvoyPin { get; set; } = "------";
    private bool _isHeadingUp = false;

    private readonly IVoiceCopilotEngine _voiceEngine;
    private readonly IRoutingEngine _routingEngine;
    private readonly ITelemetryEngine _telemetryEngine;
    private readonly RouteDeviationEngine? _deviationEngine;
    private readonly RideSimulatorService _simulatorService;
    private readonly MapPoiManager _poiManager;
    private CancellationTokenSource _rideCts;
    private DateTime _lastNetworkBroadcastTime = DateTime.MinValue;
    private Location _lastNetworkBroadcastLocation = null;
    private Location _lastAnnouncedTurn = null;
    // --- NEW: Quick local tracker for calculating other riders' speeds ---
    private readonly Dictionary<string, DateTime> _riderLastUpdateTimes = new();
    private readonly DeviceCapabilityService _deviceCapabilityService;
    private readonly MapCameraEngine? _mapCameraEngine;
    private bool? _isCurrentlyNight = null;
    private DateTime _lastSolarCheckTime = DateTime.MinValue;
    private static readonly (Color PinColor, Color RouteColor)[] RiderColors = new[]
{
    (Color.FromArgb("#00E676"), Color.FromArgb("#00B259")), // 01: Neon Green -> Emerald
    (Color.FromArgb("#FF4081"), Color.FromArgb("#D81B60")), // 02: Electric Pink -> Deep Pink
    (Color.FromArgb("#18FFFF"), Color.FromArgb("#00ACC1")), // 03: Cyan -> Ocean Blue
    (Color.FromArgb("#FF9100"), Color.FromArgb("#E65100")), // 04: Bright Orange -> Deep Orange
    (Color.FromArgb("#E040FB"), Color.FromArgb("#8E24AA")), // 05: Magenta -> Purple
    (Color.FromArgb("#536DFE"), Color.FromArgb("#3949AB")), // 06: Bright Indigo -> Royal Blue
    (Color.FromArgb("#B2FF59"), Color.FromArgb("#7CB342")), // 07: Lime -> Leaf Green
    (Color.FromArgb("#FF5252"), Color.FromArgb("#D32F2F")), // 08: Coral Red -> Crimson
    (Color.FromArgb("#40C4FF"), Color.FromArgb("#0288D1")), // 09: Sky Blue -> Cerulean
    (Color.FromArgb("#FFD740"), Color.FromArgb("#FBC02D")), // 10: Bright Amber -> Goldenrod
    (Color.FromArgb("#7C4DFF"), Color.FromArgb("#512DA8")), // 11: Bright Violet -> Deep Violet
    (Color.FromArgb("#64FFDA"), Color.FromArgb("#00897B")), // 12: Bright Teal -> Dark Teal
    (Color.FromArgb("#FF6E40"), Color.FromArgb("#D84315")), // 13: Neon Salmon -> Rust
    (Color.FromArgb("#8C9EFF"), Color.FromArgb("#3F51B5")), // 14: Light Indigo -> Indigo
    (Color.FromArgb("#69F0AE"), Color.FromArgb("#2E7D32")), // 15: Mint -> Forest Green
    (Color.FromArgb("#FF8A80"), Color.FromArgb("#C62828")), // 16: Rose -> Brick Red
    (Color.FromArgb("#84FFFF"), Color.FromArgb("#0097A7")), // 17: Aqua -> Deep Aqua
    (Color.FromArgb("#B388FF"), Color.FromArgb("#673AB7")), // 18: Lavender -> Deep Purple
    (Color.FromArgb("#FFFF00"), Color.FromArgb("#F57F17")), // 19: Laser Yellow -> Mustard
    (Color.FromArgb("#00B0FF"), Color.FromArgb("#01579B"))  // 20: Azure -> Sapphire
};

    private (Color PinColor, Color RouteColor) GetColorsForRider(string riderId)
    {
        if (string.IsNullOrEmpty(riderId)) return RiderColors[0];
        int hash = Math.Abs(riderId.GetHashCode());
        return RiderColors[hash % RiderColors.Length];
    }
    private bool ShouldBroadcastToNetwork(Location currentLoc, double speedKmh)
    {
        if (_lastNetworkBroadcastLocation == null) return true;

        double timeSinceLastSeconds = (DateTime.UtcNow - _lastNetworkBroadcastTime).TotalSeconds;

        // RULE 1: Time Fallback (Always keep the connection alive every 10 seconds)
        if (timeSinceLastSeconds >= 10) return true;

        // RULE 2: Dynamic Distance Formula
        double distSinceLastMeters = Location.CalculateDistance(_lastNetworkBroadcastLocation, currentLoc, DistanceUnits.Kilometers) * 1000;

        int minUpdateDist = groupDetails?.Settings?.MinUpdateDistanceMeters ?? 10;
        int maxUpdateDist = groupDetails?.Settings?.MaxUpdateDistanceMeters ?? 100;

        // The Math: At 0 km/h, threshold is Min (e.g. 10m). At 100+ km/h, threshold scales to Max (e.g. 100m).
        // This prevents high-speed highway driving from spamming the server, while keeping tight turns in cities accurate.
        double speedRatio = Math.Min(speedKmh, 100.0) / 100.0;
        double dynamicThresholdMeters = minUpdateDist + (speedRatio * (maxUpdateDist - minUpdateDist));

        return distSinceLastMeters >= dynamicThresholdMeters;
    }

    public LobbyPage(SignalRService signalRService,  GroupDetailsDto groupDetails)
    {
        InitializeComponent();
        BindingContext = this;
        LiveMap.NativePoiClicked += OnNativePoiClicked;

        DeviceDisplay.Current.KeepScreenOn = Preferences.Default.Get("KeepScreenOn", false);

        _signalRService = signalRService;
        _voiceEngine = IPlatformApplication.Current?.Services.GetService<IVoiceCopilotEngine>();
        _rideCache = IPlatformApplication.Current?.Services.GetService<RideStateService>();
        _routingEngine = IPlatformApplication.Current?.Services.GetService<IRoutingEngine>();
        _telemetryEngine = IPlatformApplication.Current?.Services.GetService<ITelemetryEngine>();
        _deviationEngine = IPlatformApplication.Current?.Services.GetService<RouteDeviationEngine>();
        _placeDiscoveryService = IPlatformApplication.Current?.Services.GetService<IPlaceDiscoveryService>();
        _deviceCapabilityService = IPlatformApplication.Current?.Services.GetService<DeviceCapabilityService>();
        _mapCameraEngine = IPlatformApplication.Current?.Services.GetService<MapCameraEngine>();

        _simulatorService = IPlatformApplication.Current?.Services.GetService<RideSimulatorService>();
        _poiManager = new MapPoiManager(LiveMap, _rideCache, _placeDiscoveryService);

        // Wire up the POI UI callbacks
        _poiManager.OnPoisRendered = () => MainThread.BeginInvokeOnMainThread(() => ClearPoisBtn.IsVisible = true);
        _poiManager.OnPoisCleared = () => MainThread.BeginInvokeOnMainThread(() => ClearPoisBtn.IsVisible = false);

        // Wire up the Simulator to feed fake data directly into our real hardware pipeline!
        if (_simulatorService != null)
        {
            _simulatorService.OnLocationGenerated = (loc, speed, heading) =>
            {
                // We create a fake hardware update and pump it into the exact same method the real GPS uses!
                var fakeUpdate = new LocalLocationUpdate { Location = loc, SpeedMph = speed / 1.60934, Heading = heading };
                OnLocalLocationPushedFromBackground(this, fakeUpdate);
            };
        }

        this.groupDetails = groupDetails;

#if ANDROID
        _locationTracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
        if (_locationTracker != null)
        {
            _locationTracker.LocationUpdated += OnLocalLocationPushedFromBackground;
        }
#endif

        GroupNameLabel.Text = this.groupDetails.GroupName;
        _myName = Preferences.Default.Get("username", "Unknown");
        _amIAdmin = this.groupDetails.AdminGoogleId == CurrentGoogleId;

        DestinationSearchControl.SetState(_amIAdmin, false, _amIAdmin, false);

        DrawerStatsTab.SetRidersSource(Riders);

        // Hook up SignalR events
        _signalRService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _signalRService.RosterUpdated += OnRosterUpdated;
        _signalRService.NavigationStarted += OnNavigationStarted;
        _signalRService.RiderLocationUpdated += OnRiderLocationUpdated;
        _signalRService.NavigationCancelled += OnNavigationCancelled;
        _signalRService.AlertReceived += OnAlertReceived;
        _signalRService.DestinationSet += OnSignalRDestinationSetReceived;
        _signalRService.UserJoinedAlert += OnUserJoined;
        _signalRService.UserLeftAlert += OnUserLeft;
        _signalRService.GroupDeleted += OnGroupDeleted;
        _signalRService.PttLocked += OnPttLocked;
        _signalRService.PttDenied += OnPttDenied;
        _signalRService.PttReleased += OnPttReleased;
        _signalRService.NavigationPaused += OnNavigationPaused;
        _signalRService.NavigationResumed += OnNavigationResumed;
        _signalRService.NavigationCompleted += OnNavigationCompleted;
        _signalRService.LeadRouteUpdated += OnLeadRouteUpdated;
        _signalRService.RouteDeviationAlert += OnRouteDeviationAlert;
        _signalRService.MeetupPointSet += OnMeetupPointSet;
        _signalRService.GroupSettingsUpdated += OnSettingsPushedFromServer;

        _hwButtonService = IPlatformApplication.Current?.Services.GetService<HardwareButtonService>();
        if (_hwButtonService != null)
        {
            _hwButtonService.PttPressed += OnHardwarePttPressed;
            _hwButtonService.PttReleased += OnHardwarePttReleased;
        }
        LoadLocalMapSettings();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_hasJoined) return;

        try
        {
            OnConnectionStatusChanged("Connected", Colors.MediumSeaGreen);

            if (groupDetails?.Settings != null)
            {
                if(_rideCache.CurrentSettings?.GroupName != groupDetails.Settings.GroupName)
                {
                    _rideCache.HardResetAll();
                }
                _rideCache.CurrentSettings = groupDetails.Settings;
            }

            var roster = await _signalRService.GetGroupRoster(GroupNameLabel.Text);
            if (roster != null)
            {
                OnRosterUpdated(roster);
            }

            _hasJoined = true;
            //AdminSettingsBtn.IsVisible = _amIAdmin;
            await InitializeLocalTrackingAsync();

            // Open the Convoy (Roster) tab by default when the user enters the map!
            OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty);

            if (groupDetails != null)
            {
                ConvoyPin = groupDetails.JoinCode ?? "------";
                DrawerStatsTab.SetConvoyPin(ConvoyPin);
                DrawerStatsTab.SetAdminPinCardVisible(_amIAdmin);

                // THE FIX: Push EVERYTHING through the Gatekeeper the moment you enter the room!

                if (groupDetails.CurrentState == GroupState.Navigating ||
                    groupDetails.CurrentState == GroupState.PausedBreak ||
                    groupDetails.CurrentState == GroupState.PausedHazard ||
                    groupDetails.CurrentState == GroupState.PausedMechanical)
                {
                    // 1. If the group is currently in a ride (even if paused), we MUST run the heavy 
                    // route builder so your local phone downloads and draws the active path!
                    OnNavigationStarted(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName, true);

                    // 2. If it happens to be Paused right now, safely snap the buttons into the Paused state.
                    if (groupDetails.CurrentState != GroupState.Navigating)
                    {
                        await ChangeGroupState(groupDetails.CurrentState, forceSync: true);
                    }
                }
                else
                {
                    // 3. For NotNavigating or DestinationSet, just force the Gatekeeper!
                    // If it's a brand new group, this forcefully wipes the map and resets the drawer.
                    await ChangeGroupState(groupDetails.CurrentState, forceSync: true);
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not load lobby: {ex.Message}", "OK");
            await Navigation.PopAsync();
        }
    }
    private async void OnSignalRDestinationSetReceived(double destLat, double destLng, string destName)
    {
        if (groupDetails != null)
        {
            // 1. Update the internal model silently
            groupDetails.DestLat = destLat;
            groupDetails.DestLng = destLng;
            groupDetails.DestName = destName;
        }

        // =====================================================================
        // 3. RIDE INERTIA: If we are actively riding, do NOT wipe the UI 
        // and revert to the Pre-Navigation drawer!
        // =====================================================================
        if (groupDetails?.CurrentState >= GroupState.Navigating)
        {
            return; // Silently ignore the visual downgrade. We are already driving!
        }

        // 2. Only open the Destination drawer if we were Idle!
        await ChangeGroupState(GroupState.DestinationSet, forceSync: true);
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
        _signalRService.DestinationSet -= OnSignalRDestinationSetReceived;
        _signalRService.UserJoinedAlert -= OnUserJoined;
        _signalRService.UserLeftAlert -= OnUserLeft;
        _signalRService.GroupDeleted -= OnGroupDeleted;
        _signalRService.PttLocked -= OnPttLocked;
        _signalRService.PttDenied -= OnPttDenied;
        _signalRService.PttReleased -= OnPttReleased;
        _signalRService.NavigationPaused -= OnNavigationPaused;
        _signalRService.NavigationResumed -= OnNavigationResumed;
        _signalRService.NavigationCompleted -= OnNavigationCompleted;
        _signalRService.LeadRouteUpdated -= OnLeadRouteUpdated;
        _signalRService.RouteDeviationAlert -= OnRouteDeviationAlert;
        _signalRService.MeetupPointSet -= OnMeetupPointSet;
        _signalRService.GroupSettingsUpdated -= OnSettingsPushedFromServer;

        if (_hwButtonService != null)
        {
            _hwButtonService.PttPressed -= OnHardwarePttPressed;
            _hwButtonService.PttReleased -= OnHardwarePttReleased;
        }

        _simulatorService.StopSimulation();
        _locationTracker?.StopTracking();

#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif

        if (!_isLeavingGroupPermanently)
        {
            //_ = _signalRService.LeaveLobby();
        }
        await _signalRService.StopAsync();
    }
    private async void OnNativePoiClicked(object sender, PoiClickedEventArgs e)
    {
        // Don't let standard riders or active navigating admins mess with the destination
        if (!_amIAdmin || groupDetails?.CurrentState == GroupState.Navigating) return;

        LiveMap.MapElements.Clear();
        LiveMap.Pins.Clear();

        // 1. Set the pending destination to exactly where they tapped
        _pendingDestination = e.Location;

        // 2. THE MAGIC: Because we caught the POI natively, we actually know the name of the place!
        DestinationSearchControl.InjectExternalSelection(e.Name, e.Location);

        // 3. Update the visual pin
        UpdateDestinationPin(_pendingDestination, e.Name);

        // 4. Draw the route
        var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
        if (currentLoc != null)
        {
            await CalculateAndDrawRoute(currentLoc, _pendingDestination);
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _pendingDestination]));
        }

        // Optional UX Polish: Vibrate so they know they tapped a valid location
        Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(50));
    }

    // --- SETTINGS SYNC ---
    private void OnSettingsPushedFromServer(GroupSettingsDto newSettings)
    {
        groupDetails.Settings = newSettings;
        _rideCache.CurrentSettings = newSettings;
    }

    // --- ROSTER SYNC ---
    private void OnRosterUpdated(List<Rider> roster)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var updatedRiders = new ObservableCollection<Rider>();

            foreach (var r in roster)
            {
                string displayName = r.Name;
                if (r.GoogleId == CurrentGoogleId)
                {
                    displayName += " (You)";
                    _amIAdmin = r.IsAdmin;
                    _rideCache.MyRole = r.Role; // Set Role securely from Data, not UI!
                }
                if (!r.IsOnline) displayName += " (Offline)";

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
            DrawerStatsTab.SetRidersSource(Riders);

            DrawerStatsTab.SetAdminPinCardVisible(_amIAdmin);
            TabAdminBtn.IsVisible = _amIAdmin;

            _locationTracker?.UpdateRiderCount(Riders.Count(r => r.IsOnline));
            UpdateAdminButtonsVisibility();
        });
    }

    // --- LOCATION PROCESSING & TELEMETRY ---
    private async void OnLocalLocationPushedFromBackground(object sender, LocalLocationUpdate e)
    {
        if (!_rideCache.RunningInBackground)
        {
            EvaluateDayNightCycle(e.Location);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LocationDisabledOverlay.Hide();
                if (_myPinVm != null)
                {
                    string newSpeedStr = $"{Math.Round(e.SpeedMph * 1.60934)} km/h";

                    // 1. Send the speed to the new Header Badge!
                    TelemetryHeaderControl.UpdateSpeed(newSpeedStr);

                    // THE FIX: 60-FPS Fluid Animation for the Local Pin!
                    // This tells the UI to glide the pin smoothly to the new spot over 1000ms
                    AnimatePinMovement(_myPinVm, e.Location, e.Heading, 1000);
                    _myPinVm.Speed = newSpeedStr;

                    // THE FIX: Update your OWN listing in the unified Convoy drawer
                    var myModel = Riders.FirstOrDefault(r => r.GoogleId == CurrentGoogleId);
                    if (myModel != null)
                    {
                        myModel.SpeedStr = newSpeedStr;
                        myModel.StatusStr = "Local"; // Replaces "Standby/Nearby" with "Local"
                        myModel.StatusColor = Colors.Transparent;
                    }
                }
            });
        }

        _lastKnownLocation = e.Location;

        if (ShouldBroadcastToNetwork(e.Location, e.SpeedMph * 1.60934))
        {
            _lastNetworkBroadcastTime = DateTime.UtcNow;
            _lastNetworkBroadcastLocation = e.Location;

            // Offload network call so it doesn't block the buttery-smooth UI glide!
            _ = Task.Run(async () => {
                try
                {
                    await _signalRService.UpdateLocation(groupDetails.GroupName, _myName, e.Location.Latitude, e.Location.Longitude, e.Heading);
                    AppLogger.Info("Network", $"Broadcasted location at {Math.Round(e.SpeedMph * 1.60934)} km/h");
                }
                catch (Exception ex) { AppLogger.Error("Network", ex, "Failed to broadcast location."); }
            });
        }   

        if (groupDetails?.CurrentState == GroupState.Navigating)
        {
            var lastCrumb = _rideCache.DrivenBreadcrumbs.LastOrDefault();
            if (lastCrumb == null || Location.CalculateDistance(lastCrumb, e.Location, DistanceUnits.Kilometers) > 0.05)
            {
                _rideCache.DrivenBreadcrumbs.Add(e.Location);
            }
            double speedKmh = e.SpeedMph * 1.60934;

            // =====================================================================
            // THE FIX: INJECT HARDWARE SENSORS INTO THE LOCATION OBJECT
            // MAUI's Location.Speed expects Meters Per Second (m/s)
            // =====================================================================
            e.Location.Speed = speedKmh / 3.6;
            e.Location.Course = e.Heading;

            // NOW fire the telemetry with the fully populated location!
            _ = TrimRouteVisuals(e.Location);
        }
    }

    private async Task TrimRouteVisuals(Location currentLocation)
    {
        if (_activeRouteLine == null || _rideCache.ActiveDestination == null || _rideCache.CurrentRoutePoints.Count < 2) return;
        if (_rideCts == null || _rideCts.IsCancellationRequested) return;

        try
        {
            bool voiceEnabled = Preferences.Default.Get("Map_VoiceNav", true);

            // Let the engine run the 150 lines of math on the background thread!
            var telemetry = await _routingEngine.ProcessRouteTelemetryAsync(
                currentLocation, _rideCache, _deviationEngine, _activeRouteSteps, _hasAnnouncedArrival, _lastAnnouncedTurn, voiceEnabled, _rideCts.Token);

            // 1. UPDATE CACHE & TRIPWIRES
            _rideCache.CurrentRouteIndex = telemetry.NewRouteIndex;
            _rideCache.LastOdometerLocation = currentLocation;
            _hasAnnouncedArrival = telemetry.UpdatedHasAnnouncedArrival;
            _lastAnnouncedTurn = telemetry.UpdatedLastAnnouncedTurn;

            // 2. SPEAK ALERTS (Triggered by the Engine)
            if (telemetry.SpeakDestinationReached)
                MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak("You have arrived at your destination."));

            if (telemetry.SpeakNextTurn)
                MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak(telemetry.VoiceInstructionToSpeak));

            // 3. MEETUP LOGIC
            if (_rideCache.ActiveMeetupPoint != null)
            {
                double distToMeetup = Location.CalculateDistance(currentLocation, _rideCache.ActiveMeetupPoint, DistanceUnits.Kilometers);
                if (!_haveIReachedMeetup && distToMeetup < 0.1)
                {
                    _haveIReachedMeetup = true;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _voiceEngine.Speak(_amIAdmin ? "Meetup point reached. Please wait here." : "You have reached the meetup point.");
                        _ = _signalRService.SendGroupAlert(GroupNameLabel.Text, "MeetupArrival", _myName);
                    });
                }
                else if (_haveIReachedMeetup && distToMeetup > 0.2 && _amIAdmin)
                {
                    _haveIReachedMeetup = false;
                    _ = _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, 0, 0);
                }
            }

            // 4. UPDATE UI
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_rideCts.IsCancellationRequested) return;

                TelemetryHeaderControl.UpdateTelemetryStats(
                    distText: telemetry.IsOffRoute ? (telemetry.UserMessage ?? "Rerouting...") : telemetry.DistLeftStr,
                    distColor: telemetry.IsOffRoute ? telemetry.AlertColor : Colors.DodgerBlue, //GetColorsForRider(CurrentGoogleId).RouteColor,
                    totalTravel: telemetry.TotalTravelStr,
                    totalRoute: telemetry.TotalRouteStr,
                    progressVal: telemetry.ProgressVal,
                    progressPercent: telemetry.ProgressPercentStr,
                    eta: telemetry.EtaStr,
                    isOffRoute: telemetry.IsOffRoute
                );

                NextTurnOverlay.IsVisible = !telemetry.IsOffRoute && telemetry.ShowNextTurn;
                if (NextTurnOverlay.IsVisible)
                {
                    NextTurnDistLabel.Text = telemetry.NextTurnDistStr;
                    NextTurnInstructionLabel.Text = telemetry.NextTurnInstr;
                    NextTurnIcon.Text = telemetry.NextTurnIcon;
                }
            });

            // 5. REROUTING LOGIC
            if (telemetry.ShouldReroute)
            {
                _rideCache.LastRerouteTime = DateTime.Now;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _voiceEngine.Speak("Rerouting...");
                        string newPolyline = await CalculateAndDrawRoute(currentLocation, _rideCache.ActiveDestination, _rideCache.ActiveMeetupPoint, isReroute: true);

                        if (!string.IsNullOrEmpty(newPolyline))
                        {
                            var settings = await _signalRService.GetGroupSettings(GroupNameLabel.Text);
                            if (settings != null && settings.EnableDynamicRouting)
                            {
                                if (_amIAdmin) await _signalRService.BroadcastLeadRoute(GroupNameLabel.Text, newPolyline);
                                else await _signalRService.ReportRouteDeviation(GroupNameLabel.Text, _myName);
                            }
                        }
                    }
                    catch (Exception ex) { AppLogger.Error("Routing", ex, "Failed to recalculate."); }
                }, _rideCts.Token);
            }

            // =====================================================================
            // 6. THE FIX: RUN EDGE TELEMETRY USING FLAWLESS ROAD DISTANCE
            // =====================================================================
            double currentSpeedKmh = (currentLocation.Speed ?? 0) * 3.6;
            _ = _telemetryEngine.EvaluateEdgeTelemetryAsync(
                currentLocation,
                currentSpeedKmh,
                _myName,
                GroupNameLabel.Text,
                _amIAdmin,
                telemetry.DistLeftKm); // <-- Passes the exact polyline distance!
        }
        catch (OperationCanceledException) { }
    }
    // --- NEW: Close Button Handler ---
    private async void OnCloseRideSummaryClicked(object sender, EventArgs e)
    {
        RideSummaryOverlay.IsVisible = false;

        // THE FIX: Return the app to the idle Lobby state so the
        // Search Bar and other lobby controls fully unlock again!
        await ChangeGroupState(GroupState.NotNavigating, forceSync: true);
    }
    // --- NEW: Trackers for the "Spiderweb" Meetup Routes ---
    private List<Polyline> _otherRiderRoutes = new();
    private List<Pin> _otherRiderRoutePins = new();
    // =====================================================================
    // --- NEW: CLEANUP HELPER ---
    // =====================================================================
    private void ClearOtherRiderRoutes()
    {
        foreach (var line in _otherRiderRoutes) LiveMap.MapElements.Remove(line);
        foreach (var pin in _otherRiderRoutePins) LiveMap.Pins.Remove(pin);
        _otherRiderRoutes.Clear();
        _otherRiderRoutePins.Clear();
    }

    // --- MAP & ROUTING LOGIC ---
    // =====================================================================
    // --- UPDATED: REUSABLE ROUTE DRAWING ENGINE ---
    // =====================================================================
    // We added optional parameters to specify color, name, and if it's the main route
    private async Task<string> CalculateAndDrawRoute(Location origin, Location dest, Location meetup = null, Color routeColor = null, string riderName = null, bool isMainRoute = true, bool isReroute = false)
    {
        bool includeVoiceSteps = groupDetails.CurrentState >= GroupState.Navigating;

        Color finalRouteColor = routeColor ?? (isMainRoute ? Colors.DodgerBlue : GetColorsForRider(CurrentGoogleId).RouteColor);

        // Ask the engine to do all the heavy lifting and map drawing!
        var routeUi = await _routingEngine.FetchAndBuildPolylineAsync(origin, dest, meetup, routeColor ?? finalRouteColor, includeVoiceSteps, isReroute: isReroute);

        if (routeUi != null)
        {
            _rideCache.CurrentRoutePoints = routeUi.DecodedPoints;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (isMainRoute)
                {
                    _rideCache.CurrentRouteIndex = routeUi.SpliceIndex;
                    _rideCache.OffRouteStrikeCount = 0;

                    if (PreNavDistLabel != null)
                        PreNavDistLabel.Text = $"{routeUi.DistanceKm} km, ETA {routeUi.EtaText}";

                    // =====================================================================
                    // THE FIX: SEED TELEMETRY HEADER IMMEDIATELY
                    // If we are actively navigating (or paused), take this static route 
                    // data and inject it into the active header so we don't wait for GPS movement.
                    // =====================================================================
                    if (groupDetails.CurrentState >= GroupState.Navigating && !isReroute)
                    {
                        TelemetryHeaderControl.UpdateTelemetryStats(
                            distText: $"{routeUi.DistanceKm} km",
                            distColor: Colors.DodgerBlue, // Standard route color
                            totalTravel: "0.0 km",        // We are at the starting line
                            totalRoute: $"{routeUi.DistanceKm} km",
                            progressVal: 0.0,
                            progressPercent: "0%",
                            eta: routeUi.EtaText,
                            isOffRoute: false
                        );
                    }

                    if (_activeRouteLine != null) LiveMap.MapElements.Remove(_activeRouteLine);

                    _activeRouteLine = routeUi.MapLine;
                    LiveMap.MapElements.Add(_activeRouteLine);

                    _activeRouteSteps.Clear();
                    if (routeUi.VoiceSteps != null) _activeRouteSteps.AddRange(routeUi.VoiceSteps);
                }
                else
                {
                    // Spiderweb additions
                    LiveMap.MapElements.Add(routeUi.MapLine);
                    _otherRiderRoutes.Add(routeUi.MapLine);
                }
            });
            return routeUi.EncodedPolyline;
        }
        return null;
    }
    // --- NEW VOICE NAV VARIABLES ---
    private List<RouteStep> _activeRouteSteps = new();

    private async void OnLeadRouteUpdated(string encodedPolyline)
    {
        MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak("Lead rider has updated the route. Syncing map."));

        // 1. Decode the Lead's new path (This is only the detour segment)
        var leadRoutePoints = _routingEngine.DecodeGooglePolyline(encodedPolyline);
        if (leadRoutePoints == null || leadRoutePoints.Count == 0) return;

        var detourStart = leadRoutePoints.First();
        var combinedPoints = new List<Location>();

        int seamIndex = -1;
        double minDistance = double.MaxValue;

        // =====================================================================
        // 2. THE FIX: FIND THE EXACT SEAM ON THE SHARED ROUTE!
        // Look ahead on our current map to find exactly where the Lead 
        // rider was when they recalculated, so we can attach the detour there.
        // =====================================================================
        if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Count > 0)
        {
            // Start searching from where WE currently are so we don't match a road behind us
            int searchStart = Math.Max(0, _rideCache.CurrentRouteIndex);

            for (int i = searchStart; i < _rideCache.CurrentRoutePoints.Count; i++)
            {
                double dist = Location.CalculateDistance(_rideCache.CurrentRoutePoints[i], detourStart, DistanceUnits.Kilometers);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    seamIndex = i;
                }
            }
        }

        // 3. Splicing Logic
        if (seamIndex != -1 && minDistance < 1.0) // If the Lead was within 1km of the known route
        {
            AppLogger.Info("Routing", $"Found route seam at index {seamIndex} ({Math.Round(minDistance * 1000)}m gap). Splicing detour...");

            // Keep the exact road from our driveway, past our current location, all the way to where the Lead turned!
            var historySlice = _rideCache.CurrentRoutePoints.Take(seamIndex).ToList();
            combinedPoints.AddRange(historySlice);
            combinedPoints.AddRange(leadRoutePoints);
        }
        else
        {
            // FALLBACK: The Lead rider warped somewhere completely crazy. 
            // We have to stitch a gap from our current location just to reconnect the lines.
            AppLogger.Info("Routing", "Seam too far or not found. Stitching catch-up gap.");
            var currentLoc = _rideCache.LastOdometerLocation ?? _lastKnownLocation;

            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRouteIndex > 0)
            {
                combinedPoints.AddRange(_rideCache.CurrentRoutePoints.Take(_rideCache.CurrentRouteIndex));
            }

            if (currentLoc != null)
            {
                try
                {
                    var catchUpResult = await _routingEngine.GetRouteDataAsync(currentLoc, detourStart);
                    if (catchUpResult != null && catchUpResult.DecodedPoints.Count > 0)
                        combinedPoints.AddRange(catchUpResult.DecodedPoints);
                }
                catch { }
            }
            combinedPoints.AddRange(leadRoutePoints);
        }

        // 4. Update the Cache
        _rideCache.CurrentRoutePoints = combinedPoints;
        _rideCache.OffRouteStrikeCount = 0;

        // THE FIX: Do NOT reset _rideCache.CurrentRouteIndex to 0! 
        // We are exactly where we were, and the simulator/GPS should continue normally!

        // 5. Redraw the Map
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var oldLines = LiveMap.MapElements.OfType<Polyline>().ToList();
            foreach (var line in oldLines) LiveMap.MapElements.Remove(line);

            // Always draw the main route in Dodger Blue
            _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 22f };
            foreach (var coord in _rideCache.CurrentRoutePoints) _activeRouteLine.Geopath.Add(coord);
            LiveMap.MapElements.Add(_activeRouteLine);
        });
    }

    private void OnRouteDeviationAlert(string userName)
    {
        MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak($"{userName} has diverted from the route."));
    }
    private async Task GenerateMeetupPointAsync()
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.OtherRiderLocations.Count == 0 || _rideCache.ActiveDestination == null) return;

        MainThread.BeginInvokeOnMainThread(() => GlobalLoadingOverlay.Show("Calculating Convergence..."));
        _voiceEngine.Speak("Calculating a safe meetup point for the group. Please wait.");

        try
        {
            // Ask the RoutingEngine to run the hybrid Convergence math!
            var meetupPoint = await _routingEngine.CalculateDynamicMeetupPointAsync();

            // If the Engine returns null, everyone is safe. Clear the pin!
            if (meetupPoint == null)
            {
                await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, 0, 0);
            }
            else
            {
                await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, meetupPoint.Latitude, meetupPoint.Longitude);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Meetup Error: {ex.Message}");
            await DisplayAlert("Convergence Error", "Could not calculate a safe merge point.", "OK");
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() => GlobalLoadingOverlay.Hide());
        }
    }
    private void OnMeetupPointSet(double lat, double lng)
    {
        _haveIReachedMeetup = false;
        _ridersAtMeetup.Clear();

        if (lat == 0 && lng == 0)
        {
            _rideCache.ActiveMeetupPoint = null;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var oldMeetup = LiveMap.Pins.FirstOrDefault(p => p.Label == "Meetup Point");
                if (oldMeetup != null) LiveMap.Pins.Remove(oldMeetup);
                ClearOtherRiderRoutes();
            });
            return;
        }

        _rideCache.ActiveMeetupPoint = new Location(lat, lng);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var oldPin = LiveMap.Pins.FirstOrDefault(p => p.Label == "Meetup Point");
            if (oldPin != null) LiveMap.Pins.Remove(oldPin);

            LiveMap.Pins.Add(new Pin { Label = "Meetup Point", Location = _rideCache.ActiveMeetupPoint, Type = PinType.Generic });
        });

        // THE FIX: Do NOT recalculate the Main Route. It breaks the Odometer!
        // Just draw the Spiderwebs for the lost riders.
        bool amILead = _rideCache.MyRole == "Lead" || (_amIAdmin && string.IsNullOrEmpty(_rideCache.CurrentSettings?.LeadRiderGoogleId));

        if (amILead)
        {
            MainThread.BeginInvokeOnMainThread(() => ClearOtherRiderRoutes());

            foreach (var rider in _rideCache.OtherRiderLocations)
            {
                var colorProfile = GetColorsForRider(rider.Key);

                // Spiderweb strictly from the Rider -> Meetup Point
                _ = CalculateAndDrawRoute(
                    origin: rider.Value,
                    dest: _rideCache.ActiveMeetupPoint,
                    meetup: null,
                    routeColor: colorProfile.RouteColor,
                    riderName: rider.Key,
                    isMainRoute: false);
            }
        }
    }   
    private async void OnGenerateMeetupClicked(object sender, EventArgs e)
    {
        // Manual override for Late Joiners
        await GenerateMeetupPointAsync();
    }

    // --- UI/DRAWER PHYSICS ---
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
                double newTranslation = _currentDrawerTranslation + e.TotalY;
                ActionDrawer.TranslationY = Math.Max(0, Math.Min(newTranslation, maxTranslation));
                break;
            case GestureStatus.Completed:
                if (ActionDrawer.TranslationY < maxTranslation * 0.4)
                    ActionDrawer.TranslateTo(0, 0, 250, Easing.CubicOut);
                else
                    ActionDrawer.TranslateTo(0, maxTranslation, 250, Easing.CubicOut);
                break;
        }
    }

    private void OnDrawerHandleTapped(object sender, TappedEventArgs e)
    {
        double maxTranslation = _drawerFullHeight - _drawerPeekHeight;
        if (ActionDrawer.TranslationY < maxTranslation * 0.5)
            ActionDrawer.TranslateTo(0, maxTranslation, 250, Easing.CubicOut);
        else
            ActionDrawer.TranslateTo(0, 0, 250, Easing.CubicOut);
    }

    private void OnDrawerTabClicked(object sender, EventArgs e)
    {
        TabActionsBtn.BackgroundColor = Colors.Transparent; TabActionsBtn.TextColor = Colors.Gray;
        TabStatsBtn.BackgroundColor = Colors.Transparent; TabStatsBtn.TextColor = Colors.Gray;
        TabMapSettingsBtn.BackgroundColor = Colors.Transparent; TabMapSettingsBtn.TextColor = Colors.Gray;
        TabAdminBtn.BackgroundColor = Colors.Transparent; TabAdminBtn.TextColor = Colors.Gray;

        DrawerActionsTab.IsVisible = false;
        DrawerStatsTab.IsVisible = false;
        DrawerMapSettingsTab.IsVisible = false;
        DrawerAdminTab.IsVisible = false;

        // Safety/Actions only available once we actually start driving
        if (sender == TabActionsBtn && groupDetails.CurrentState >= GroupState.Navigating)
        {
            TabActionsBtn.BackgroundColor = Colors.DodgerBlue;
            TabActionsBtn.TextColor = Colors.White;
            DrawerActionsTab.IsVisible = true;
        }
        else if (sender == TabAdminBtn && _amIAdmin) // Visible strictly to Admins
        {
            TabAdminBtn.BackgroundColor = Colors.DodgerBlue;
            TabAdminBtn.TextColor = Colors.White;
            DrawerAdminTab.IsVisible = true;
        }
        else if (sender == TabMapSettingsBtn) // Available to everyone immediately
        {
            TabMapSettingsBtn.BackgroundColor = Colors.DodgerBlue;
            TabMapSettingsBtn.TextColor = Colors.White;
            DrawerMapSettingsTab.IsVisible = true;
        }
        else // Fallback & Default is the Unified Convoy/Stats Tab
        {
            TabStatsBtn.BackgroundColor = Colors.DodgerBlue;
            TabStatsBtn.TextColor = Colors.White;
            DrawerStatsTab.IsVisible = true;
        }

        if (ActionDrawer.TranslationY >= (_drawerFullHeight - _drawerPeekHeight) - 10)
            ActionDrawer.TranslateTo(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
    }

    // --- STATE MACHINE ---
    // 🔄 REPLACE entire method
    private async Task ChangeGroupState(GroupState newState, string triggerUser = "", string reason = "", bool forceSync = false)
    {
        if (!forceSync && this.groupDetails.CurrentState == newState) return;

        // =====================================================================
        // 4. THE ULTIMATE SHIELD: Prevent silent background loops from 
        // destroying an active navigation session.
        // =====================================================================
        if (this.groupDetails.CurrentState >= GroupState.Navigating && newState < GroupState.Navigating)
        {
            // If the state is downgrading, but there is no explicit human "triggerUser", 
            // it is a network glitch. Reject it!
            if (string.IsNullOrEmpty(triggerUser) && !forceSync)
            {
                AppLogger.Info("State", $"Blocked illegal state downgrade to {newState} due to missing human trigger.");
                return;
            }
        }

        this.groupDetails.CurrentState = newState;
        _stateStartTime = DateTime.Now;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (newState)
            {
                case GroupState.DestinationSet:
                    OnDestinationSet(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                    DestinationSearchControl.SetDestinationText(groupDetails.DestName);
                    // Toggle Headers
                    IdleHeader.IsVisible = false;
                    PreNavigationHeader.IsVisible = true;
                    TelemetryHeaderControl.IsVisible = false;

                    DestinationSearchControl.SetState(isVisible: _amIAdmin, isReadOnly: true, showBanner: false, showConfirm: false);

                    ActionDrawer.IsVisible = true;
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;
                    FloatingMapControls.IsVisible = true;

                    break;

                case GroupState.NotNavigating:
                case GroupState.Completed:
                    // Toggle Headers
                    IdleHeader.IsVisible = true;
                    PreNavigationHeader.IsVisible = false;
                    TelemetryHeaderControl.IsVisible = false;

                    // THE FIX: Show appropriate header message based on Role
                    AdminIdleHeader.IsVisible = _amIAdmin;
                    RiderIdleHeader.IsVisible = !_amIAdmin;

                    ActionDrawer.IsVisible = true;
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;

                    FloatingMapControls.IsVisible = false;
#if DEBUG
                    //SimSpeedFrame.IsVisible = false;
#endif
                    DestinationSearchControl.Reset();
                    DestinationSearchControl.SetState(isVisible: _amIAdmin, isReadOnly: false, showBanner: _amIAdmin, showConfirm: true);

                    // BULLETPROOF CLEANUP
                    NextTurnOverlay.IsVisible = false;
                    LiveMap.MapElements.Clear();
                    LiveMap.Pins.Clear();
                    _activeRouteLine = null;
                    _activeRouteSteps.Clear();
                    ClearOtherRiderRoutes();
                    _poiManager.ClearTemporaryPois();

                    _locationTracker?.StopTracking();
                    _simulatorService.StopSimulation();

                    FitMapToBounds();
                    ToggleNavigationPerspective(false);
#if ANDROID
                    MainActivity.IsInNavigationMode = false;
#endif
                    if (newState == GroupState.Completed)
                        _voiceEngine.Speak($"Navigation completed by {triggerUser}. Great ride!");

                    UpdateAdminButtonsVisibility();

                    //if (MySpeedLabel != null) MySpeedLabel.Text = "0 km/h";
                    break;

                case GroupState.Navigating:
                    // Toggle Headers
                    IdleHeader.IsVisible = false;
                    PreNavigationHeader.IsVisible = false;
                    TelemetryHeaderControl.IsVisible = true;

                    // Auto-switch drawer to the "Safety" Actions tab
                    OnDrawerTabClicked(TabActionsBtn, EventArgs.Empty);

                    DestinationSearchControl.SetState(isVisible: false, isReadOnly: false, showBanner: false, showConfirm: false);

                    FloatingMapControls.IsVisible = true;
#if DEBUG
                    //SimSpeedFrame.IsVisible = true;
#endif

                    TabAdminBtn.IsVisible = _amIAdmin;
                    TelemetryHeaderControl.SetDestinationName(groupDetails.DestName);
                    SetActionButtonsEnabled(true);
                    _locationTracker?.StartTracking(GroupNameLabel.Text, Riders.Count(x => x.IsOnline));

#if ANDROID
                    MainActivity.IsInNavigationMode = true;
#endif
                    if (string.IsNullOrEmpty(triggerUser))
                        _voiceEngine.Speak("Navigation active. Ride safe!");
                    break;

                case GroupState.PausedBreak:
                case GroupState.PausedHazard:
                case GroupState.PausedMechanical:
                    _locationTracker?.StopTracking();
                    SetActionButtonsEnabled(false);
#if DEBUG
                    //SimSpeedFrame.IsVisible = false;
#endif

                    string context = newState == GroupState.PausedBreak ? "for a break" :
                                     newState == GroupState.PausedHazard ? "due to a hazard" :
                                     "for mechanical repairs";

                    string spokenReason = string.IsNullOrEmpty(reason) ? context : reason;
                    _voiceEngine.Speak($"Navigation paused by {triggerUser} {spokenReason}. Tracking suspended.");

                    ActionDrawer.TranslateToAsync(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
                    break;
            }
            UpdateAdminButtonsVisibility();
        });
    }

    // --- EVENT TRIGGERS ---
    // 🔄 REPLACE entire method
    // 🔄 REPLACE entire method
    private async void OnDestinationSet(double destLat, double destLng, string destName)
    {
        GlobalLoadingOverlay.Show("Drawing Route...");
        try
        {
            _rideCache.ResetNavigationState();
            _rideCache.ActiveDestination = new Location(destLat, destLng);
            _rideCache.ActiveDestinationName = destName;

            // THE FIX: Redrop the Destination Pin when returning to the Lobby!
            UpdateDestinationPin(_rideCache.ActiveDestination, destName);

            var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
            if (currentLoc != null)
            {
                // This updates PreNavDistLabel with the exact distance & ETA
                await CalculateAndDrawRoute(currentLoc, _rideCache.ActiveDestination);
                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _rideCache.ActiveDestination]));
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                PreNavDestLabel.Text = destName;

                if (_amIAdmin)
                {
                    StartJourneyButton.IsVisible = true;
                    StartJourneyButton.IsEnabled = true;
                    if (currentLoc == null) PreNavDistLabel.Text = "Waiting for GPS to calc route...";
                }
                else
                {
                    StartJourneyButton.IsVisible = false;
                    if (currentLoc == null) PreNavDistLabel.Text = "Waiting for Admin to Start";
                    else PreNavDistLabel.Text += " (Waiting for Admin)";
                }
            });
        }
        finally { GlobalLoadingOverlay.Hide(); }
    }

    private async void OnNavigationStarted(double destLat, double destLng, string destName, bool isSyncRequired = false)
    {

        AppLogger.ResetRideCorrelationId();
        _rideCts?.Cancel();
        _rideCts = new CancellationTokenSource();

        AppLogger.Info("Navigation", $"Starting route to {destName}...");

        _rideCache.ActiveDestination = new Location(destLat, destLng);
        _rideCache.ActiveDestinationName = destName;
        DestinationSearchControl.SetDestinationText(destName);

        _rideCache.ResetTelemetryState();

        // THE FIX: Reset the arrival tripwire for the new ride!
        _hasAnnouncedArrival = false;

        await ChangeGroupState(GroupState.Navigating, _myName, forceSync: isSyncRequired);

        Location loc;
#if DEBUG
        loc = _lastKnownLocation ?? await Geolocation.Default.GetLastKnownLocationAsync();
#else
        loc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
#endif

        if (loc != null)
        {
            // THE FIX: Ensure UI properties on custom header components are touched strictly on the main thread!
            MainThread.BeginInvokeOnMainThread(() =>
            {
                TelemetryHeaderControl.SetOriginCoordinates(loc.Latitude, loc.Longitude);
            });

            await CalculateAndDrawRoute(loc, _rideCache.ActiveDestination);
            // THE FIX: Start navigation zoomed in and pointing North instead of zooming out to FitMapToBounds!
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_myPinVm != null) _myPinVm.IsAutoCentering = true;

                FitMapToBounds();

                ToggleNavigationPerspective(true);
            });

#if DEBUG
            //if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any())
            //{
            //    _ = _simulatorService?.StartSimulationAsync(groupDetails.CurrentState, _rideCts.Token);
            //}
#endif
        }
        if (!isSyncRequired)
            _voiceEngine.Speak($"Navigation started to {destName}. Ride safe!");
    }

    private async void OnNavigationCompleted(string adminName)
    {
        string currentGroupName = GroupNameLabel.Text;

        // Call the Telemetry Engine to process the final stats
        var finalSummary = await _telemetryEngine.ProcessAndSaveRideTelemetryAsync(currentGroupName);

        // 2. SAFELY UPDATE THE MAP AND THE NEW SUMMARY UI ON THE MAIN THREAD
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ClearOtherRiderRoutes();
            LiveMap.MapElements.Clear();
            _activeRouteLine = null;

            if (finalSummary != null)
            {
                // THE FIX: Pass data cleanly to the component!
                RideSummaryOverlay.Show(
                    destination: finalSummary.DestinationName,
                    distance: $"{finalSummary.TotalDistanceKm} km",
                    movingTime: finalSummary.MovingTime.ToString(@"hh\:mm\:ss"),
                    avgSpeed: $"{finalSummary.AverageMovingSpeedKmh} km/h",
                    topSpeed: $"{finalSummary.TopSpeedKmh} km/h",
                    totalTime: finalSummary.TotalElapsedTime.ToString(@"hh\:mm\:ss")
                );
            }
        });

        await ChangeGroupState(GroupState.Completed, adminName);
    }

    private async void OnNavigationCancelled()
    {
        // 1. Kill background loops instantly for the receiving riders too!
        _rideCts?.Cancel();
        _simulatorService?.StopSimulation();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            ClearOtherRiderRoutes();
            OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty); // Reset drawer to Convoy tab
        });

#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif
        _locationTracker?.StopTracking();
        _rideCache.HardResetAll();
        await ChangeGroupState(GroupState.NotNavigating, forceSync: true);
    }

    private async void OnResetDestinationClicked(object sender, EventArgs e)
    {
        // 1. THE FIX: Kill all background loops INSTANTLY so they 
        // don't run late and overwrite our UI cleanup!
        _rideCts?.Cancel();
        _simulatorService?.StopSimulation();

        if (groupDetails != null)
        {
            DestinationSearchControl.Reset();

            if (_activeRouteLine != null)
            {
                LiveMap.MapElements.Remove(_activeRouteLine);
                _activeRouteLine = null;
                _activeRouteSteps.Clear();
            }

            var oldPins = LiveMap.Pins.Where(p => p.Type == PinType.Place).ToList();
            foreach (var p in oldPins) LiveMap.Pins.Remove(p);

            _rideCache.HardResetAll();
        }

        // 2. THE FIX: Snap the drawer back to the standard Convoy Roster view
        OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty);

        await ChangeGroupState(GroupState.NotNavigating, _myName);
        await _signalRService.CancelGroupNavigation(GroupNameLabel.Text);
    }

    // --- REMAINING UTILITIES ---
    private async void OnStartJourneyClicked(object sender, EventArgs e)
    {
        GlobalLoadingOverlay.Show("Starting Navigation...");
        try
        {
            StartJourneyButton.IsEnabled = false;
            await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _rideCache.ActiveDestination.Latitude, _rideCache.ActiveDestination.Longitude, groupDetails.DestName);
        }
        finally { GlobalLoadingOverlay.Hide(); }
    }

    private async void OnCopyPinClicked(object sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(ConvoyPin);
        await DisplayAlert("Copied", $"Convoy PIN '{ConvoyPin}' copied to clipboard!", "OK");
    }

    private void SetActionButtonsEnabled(bool isEnabled)
    {
        DrawerActionsTab.IsEnabled = isEnabled;
        DrawerActionsTab.Opacity = isEnabled ? 1.0 : 0.4;
    }

    private void FitMapToBounds(List<Location> points = null)
    {
        var targetPoints = points ?? MapPins.Select(p => p.Location).ToList();

        Task.Run(() =>
        {
            var region = _mapCameraEngine.CalculateBoundingRegion(targetPoints);
            if (region != null)
                MainThread.BeginInvokeOnMainThread(() => LiveMap.MoveToRegion(region));
        });
    }
    private async void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DrawerStatsTab.UpdateConnectionStatus(status, color);
        });

        if (color == Colors.MediumSeaGreen)
        {
            // 1. Fetch data safely off the main thread
            var groupName = await MainThread.InvokeOnMainThreadAsync(() => GroupNameLabel.Text);
            var fetchedDetails = await _signalRService.GetGroupDetails(groupName);

            if (fetchedDetails != null)
            {
                // 2. Safely marshal all State Math back to the Main Thread
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var previousState = this.groupDetails?.CurrentState ?? GroupState.NotNavigating;

                    // =====================================================================
                    // 1. ADMIN SOURCE OF TRUTH (Self-Healing Server)
                    // =====================================================================
                    if (_amIAdmin && previousState >= GroupState.Navigating && fetchedDetails.CurrentState < GroupState.Navigating)
                    {
                        AppLogger.Info("Network", "Server lost active ride state. Admin is enforcing Navigating state.");
                        await _signalRService.StartGroupNavigation(groupName, groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                        return; // Keep local state running seamlessly
                    }

                    // Update local memory with the server's truth
                    this.groupDetails = fetchedDetails;

                    if (previousState != fetchedDetails.CurrentState)
                    {
                        // =====================================================================
                        // 2. STANDARD RIDER CATCH-UP LOGIC
                        // =====================================================================

                        if (previousState < GroupState.Navigating && fetchedDetails.CurrentState >= GroupState.Navigating)
                        {
                            // A. Missed the Ride Start! 
                            // We MUST call OnNavigationStarted so it actually fetches Google Maps and draws the line!
                            AppLogger.Info("Network", "Catching up: Ride started while offline.");
                            OnNavigationStarted(fetchedDetails.DestLat, fetchedDetails.DestLng, fetchedDetails.DestName, isSyncRequired: true);
                        }
                        else if (previousState < GroupState.DestinationSet && fetchedDetails.CurrentState == GroupState.DestinationSet)
                        {
                            // B. Missed the Destination Set!
                            // Call OnDestinationSet so it draws the grey Pre-Nav route line.
                            AppLogger.Info("Network", "Catching up: Destination set while offline.");
                            OnDestinationSet(fetchedDetails.DestLat, fetchedDetails.DestLng, fetchedDetails.DestName);
                        }
                        else
                        {
                            // C. Standard State Change (Pauses, Completions, or Ride Stops)
                            // Note: By dropping the "Rider Inertia" block here, standard riders will correctly 
                            // stop their ride if the Admin hit "Finish" while they were in a tunnel!
                            AppLogger.Info("Network", $"Syncing state to {fetchedDetails.CurrentState}");
                            await ChangeGroupState(fetchedDetails.CurrentState, forceSync: true);
                        }
                    }
                });
            }
        }
    }
    private async void OnMapClicked(object sender, MapClickedEventArgs e)
    {
        if (!_amIAdmin || groupDetails?.CurrentState == GroupState.Navigating) return;

        LiveMap.MapElements.Clear();
        LiveMap.Pins.Clear();

        _pendingDestination = e.Location;
        try
        {
            var placemarks = await Geocoding.Default.GetPlacemarksAsync(e.Location.Latitude, e.Location.Longitude);
            var placemark = placemarks?.FirstOrDefault();
            if (placemark != null)
            {
                DestinationSearchControl.InjectExternalSelection($"{placemark.FeatureName} {placemark.Thoroughfare}, {placemark.Locality}".Trim(' ', ','), e.Location);
            }
            else
            {
                DestinationSearchControl.InjectExternalSelection($"{e.Location.Latitude:F4}, {e.Location.Longitude:F4}", e.Location);
            }
        }
        catch { DestinationSearchControl.InjectExternalSelection($"{e.Location.Latitude:F4}, {e.Location.Longitude:F4}", e.Location); }

        UpdateDestinationPin(_pendingDestination, "Selected Destination");
        var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
        await CalculateAndDrawRoute(currentLoc, _pendingDestination);
        MainThread.BeginInvokeOnMainThread(async () => FitMapToBounds([currentLoc, _pendingDestination]));

    }
    private void UpdateDestinationPin(Location location, string label)
    {
        var oldDest = LiveMap.Pins.FirstOrDefault(p => p.Label != "You" && p.Type == PinType.Place);
        if (oldDest != null) LiveMap.Pins.Remove(oldDest);

        LiveMap.Pins.Add(new Pin() { Label = label, Location = location, Type = PinType.Place });
    }
    // --- REPLACED MAP CAMERA MODES ---
    private void OnRecenterMapClicked(object sender, EventArgs e)
    {
        if (_lastKnownLocation != null)
        {
            // THE FIX: Return to default 0.5km zoom and gently rotate North
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(_lastKnownLocation, Distance.FromKilometers(0.5)));
            //LiveMap.RotateTo(0, 500, Easing.SinInOut);
            ToggleNavigationPerspective(true);

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

        // 2. THE FIX: Reset the pin to the absolute center of the map!
        ToggleNavigationPerspective(false);

        await Task.Delay(50);
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
            if (alertType == "MeetupArrival")
            {
                if (_amIAdmin)
                {
                    // Clean up their name (removes the "(Offline)" tag if it's there)
                    var riderModel = Riders.FirstOrDefault(r => r.GoogleId == senderName || r.Name.StartsWith(senderName));
                    string cleanName = riderModel?.Name?.Replace(" (Offline)", "") ?? senderName;

                    _voiceEngine.Speak($"{cleanName} has reached the meetup point.");
                }
                return; // Exit early so we don't show the red Emergency overlay!
            }
            if (alertType == "Lagging" || alertType == "Splinter" || alertType == "VoicePrompt")
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));
                _voiceEngine.Speak(senderName);
                return;
            }
            int durationSeconds = 5;
            string voiceMessage = "";

            SetActionButtonsEnabled(false);

            if (alertType == "Emergency")
            {
                voiceMessage = $"Emergency Stop triggered by {senderName}. Please pull over safely immediately.";
            }
            else if (alertType == "Refuel")
            {
                voiceMessage = $"{senderName} needs a refuel break. Prepare to stop at the next gas station.";
            }
            else if (alertType == "Rest")
            {
                voiceMessage = $"{senderName} requested a rest stop. Prepare to pull over soon.";
            }

            _voiceEngine.Speak(voiceMessage);

            // THE FIX: Let the component handle its own 3-second flashing animation!
            await SensoryAlertOverlay.TriggerAlertAsync(alertType, senderName);

            SetActionButtonsEnabled(true);
        });
    }
    private void OnUserJoined(string username) => _voiceEngine.Speak($"{username} has joined the group.");
    private void OnUserLeft(string username) => _voiceEngine.Speak($"{username} has left the group.");
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
            _rideCache.HardResetAll();
            await _signalRService.LeaveGroup(CurrentGoogleId);
            await Navigation.PopAsync();
        }
    }
    private void OnOpenSettingsClicked(object sender, EventArgs e) => AppInfo.Current.ShowSettingsUI();
    private void OnRetryLocationClicked(object sender, EventArgs e) => InitializeLocalTrackingAsync();
    private async void OnRiderTapped(object sender, Rider selectedRider)
    {
        if (!_amIAdmin)
        {
            await DisplayAlert("Permission Denied", "Only the Admin can assign roles or view emergency info.", "OK");
            return;
        }

        // We no longer have to cast e.Parameter, the component did the work for us!
        if (selectedRider == null)
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
    private async void OnPauseNavClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        string reason = await DisplayActionSheet("Reason for Pause?", "Cancel", null,
            "Fuel Stop", "Food/Rest Break", "Scenic Viewpoint", "Mechanical Issue", "Wait for Stragglers");

        if (reason == "Cancel" || string.IsNullOrEmpty(reason)) return;

        GlobalLoadingOverlay.Show("Pausing Route...");
        try
        {
            await _signalRService.PauseGroupNavigation(GroupNameLabel.Text, reason, _myName);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }
    private async void OnResumeJourneyClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        GlobalLoadingOverlay.Show("Resuming...");
        try
        {
            await _signalRService.ResumeGroupNavigation(GroupNameLabel.Text, _myName);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async void OnCompleteNavClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        bool confirm = await DisplayAlert("Complete Route", "Are you sure you want to end this journey? This will stop navigation for everyone.", "Finish Ride", "Cancel");
        if (!confirm) return;

        GlobalLoadingOverlay.Show("Completing Route...");
        try
        {
            await _signalRService.CompleteGroupNavigation(GroupNameLabel.Text, _myName);
            _rideCts?.Cancel();
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
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
            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any())
            {
                _ = _simulatorService?.StartSimulationAsync(groupDetails.CurrentState, _rideCts.Token);
            }
#endif
        }

        _voiceEngine.Speak($"Resuming Navigation to {groupDetails.DestName}. Ride safe!");
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

        ToggleNavigationPerspective(true);
    }
    private void OnAdminSettingsClicked(object sender, EventArgs e)
    {
        if (groupDetails?.Settings != null)
        {
            var s = groupDetails.Settings;

            // THE FIX: Launch the Unified Component in Edit Mode, passing the current server values!
            // Note: Pitstop slider expects km, but the server stores meters, so we divide by 1000.
            ConvoySettingsOverlay.ShowForEdit(
                size: s.MaxGroupSize,
                lag: s.MaxLagDistanceMeters,
                splinter: s.SplinterWarningDistanceMeters,
                pitstop: s.PitstopDistanceMeters / 1000,
                dynamicRouting: s.EnableDynamicRouting,
                minUpdate: s.MinUpdateDistanceMeters,
                maxUpdate: s.MaxUpdateDistanceMeters);
        }
    }

    private async void OnSettingsSubmitted(object sender, ConvoySettingsSubmittedEventArgs e)
    {
        // THE FIX: LobbyPage ONLY handles Edit mode. (MainPage handles Creation)
        if (e.IsCreationMode) return;

        AppLogger.Info("Settings", $"Saving lag: {e.MaxLagDistanceMeters}m, Splinter: {e.SplinterWarningDistanceMeters}m");

        // Send the updated packet to the server
        await _signalRService.UpdateGroupSettings(GroupNameLabel.Text, new GroupSettingsDto
        {
            MaxLagDistanceMeters = e.MaxLagDistanceMeters,
            SplinterWarningDistanceMeters = e.SplinterWarningDistanceMeters,
            MaxGroupSize = e.MaxGroupSize,
            PitstopDistanceMeters = e.PitstopDistanceMeters * 1000, // convert slider km back to meters!
            MinUpdateDistanceMeters = e.MinBroadcastDistanceMeters,
            MaxUpdateDistanceMeters = e.MaxBroadcastDistanceMeters,
            ArrivalGeofenceMeters = 1000,
            EnableDynamicRouting = e.EnableDynamicRouting,
            LeadRiderGoogleId = CurrentGoogleId
        });

        Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100));
    }
    private void OnPttLocked(string speakerName)
    {
        _currentSpeaker = speakerName;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (speakerName == _myName)
            {
                PttOverlay.ShowMicOpen();

                _pttCts?.Cancel();
                _pttCts = new CancellationTokenSource();
                _ = RunPttTimeoutAsync(_pttCts.Token);

                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));
                _voiceEngine.Speak("You can now speak.");
            }
            else
            {
                _pttCts?.Cancel();
                PttOverlay.ShowListening(speakerName);
            }
        });
    }
    private async Task RunPttTimeoutAsync(CancellationToken token)
    {
        _pttTimeRemaining = 30;
        try
        {
            while (_pttTimeRemaining > 0 && !token.IsCancellationRequested)
            {
                MainThread.BeginInvokeOnMainThread(() => PttOverlay.UpdateCountdown(_pttTimeRemaining));
                await Task.Delay(1000, token);
                _pttTimeRemaining--;
            }

            if (_pttTimeRemaining <= 0 && !token.IsCancellationRequested)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PttOverlay.ShowMaximumTimeReached();
                    _voiceEngine.Speak("Microphone closed.");
                });

                if (_currentSpeaker == _myName)
                {
                    await _signalRService.ReleasePtt(GroupNameLabel.Text, _myName);
                }
            }
        }
        catch (TaskCanceledException) { }
    }
    private void OnPttDenied(string activeSpeaker)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _voiceEngine.Speak("Channel busy.");
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
        });
    }
    private void OnPttReleased()
    {
        _currentSpeaker = string.Empty;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _pttCts?.Cancel();
            PttOverlay.Hide();
        });
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
    private async void OnLaunchNativeNavClicked(object sender, EventArgs e)
    {
        if (_rideCache.ActiveDestination == null) return;

        try
        {
            if (DeviceInfo.Platform == DevicePlatform.Android)
            {
                await Launcher.OpenAsync($"google.navigation:q={_rideCache.ActiveDestination.Latitude},{_rideCache.ActiveDestination.Longitude}&mode=d");
            }
            else if (DeviceInfo.Platform == DevicePlatform.iOS)
            {
                bool hasGoogleMaps = await Launcher.TryOpenAsync($"comgooglemaps://?daddr={_rideCache.ActiveDestination.Latitude},{_rideCache.ActiveDestination.Longitude}&directionsmode=driving");
                if (!hasGoogleMaps)
                {
                    await Launcher.OpenAsync($"http://maps.apple.com/?daddr={_rideCache.ActiveDestination.Latitude},{_rideCache.ActiveDestination.Longitude}&dirflg=d");
                }
            }
        }
        catch (Exception) { await DisplayAlert("Error", "Could not open map.", "OK"); }
    }
    private void MapPinClicked(RiderPin pin)
    {
        // Handle pin click
    }
    private async void OnDestinationPreviewRequested(object sender, PlaceSelectedEventArgs e)
    {
        _pendingDestination = e.Location;
        UpdateDestinationPin(_pendingDestination, e.Name);

        var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
        if (currentLoc != null)
        {
            await CalculateAndDrawRoute(currentLoc, _pendingDestination);
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _pendingDestination]));
        }
    }

    private async void OnDestinationConfirmed(object sender, PlaceSelectedEventArgs e)
    {
        string destName = e.Name;
        _rideCache.ActiveDestination = e.Location;
        groupDetails.DestLng = e.Location.Longitude;
        groupDetails.DestLat = e.Location.Latitude;
        groupDetails.DestName = destName;

        await ChangeGroupState(GroupState.DestinationSet, _myName);
        await _signalRService.SetGroupDestination(GroupNameLabel.Text, e.Location.Latitude, e.Location.Longitude, destName);

        //if (_amIAdmin) _ = GenerateMeetupPointAsync();
    }

    private void OnDestinationCleared(object sender, EventArgs e)
    {
        LiveMap.MapElements.Remove(_activeRouteLine);
        LiveMap.MapElements.Clear();
        _activeRouteLine = null;
        _activeRouteSteps.Clear();

        var oldPins = LiveMap.Pins.Where(p => p.Type == PinType.Place && p.Label != "You").ToList();
        foreach (var p in oldPins) LiveMap.Pins.Remove(p);

        if (groupDetails?.CurrentState == GroupState.DestinationSet)
        {
            _rideCache.HardResetAll();
            _ = ChangeGroupState(GroupState.NotNavigating, _myName);
        }
    }


    private void OnRiderLocationUpdated(string riderId, double lat, double lng, double heading)
    {
        Task.Run(() =>
        {
            var status = _telemetryEngine.CalculateRiderStatus(riderId, new Location(lat, lng), _lastKnownLocation, _rideCache, groupDetails?.Settings);

            if (_rideCache.RunningInBackground) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var riderModel = Riders.FirstOrDefault(r => r.Name != null && r.Name.StartsWith(riderId));
                if (riderModel != null)
                {
                    riderModel.SpeedStr = status.SpeedStr;
                    riderModel.StatusStr = status.StatusStr;
                    riderModel.StatusColor = status.StatusColor;
                }

                if (_riderViewModels.TryGetValue(riderId, out var existingVm))
                {
                    existingVm.Location = status.InterpolatedLocation;
                    existingVm.Heading = heading;
                    existingVm.Speed = status.SpeedStr;
                }
                else
                {
                    var colorProfile = GetColorsForRider(riderId);
                    var newVm = new RiderPin(MapPinClicked) { Username = riderId, Speed = status.SpeedStr, Location = status.InterpolatedLocation, Heading = heading, PinColor = colorProfile.PinColor, ZIndex = 50F };
                    _riderViewModels.TryAdd(riderId, newVm);
                    MapPins.Add(newVm);
                }
            });
        });
    }
    private async Task InitializeLocalTrackingAsync()
    {
        GlobalLoadingOverlay.Show("Initializing...");
        try
        {
            bool hasPermissions = await _deviceCapabilityService.RequestRequiredPermissionsAsync();
            if (!hasPermissions) { MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.Show()); return; }

            await _deviceCapabilityService.RequestBackgroundExecutionOverridesAsync(this);

            var currentLocation = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(5)));
            if (currentLocation != null)
            {
                _lastKnownLocation = currentLocation;
                EvaluateDayNightCycle(currentLocation);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    LocationDisabledOverlay.Hide();
                    DrawerStatsTab.IsVisible = true;

                    if (_myPinVm == null)
                    {
                        var myColors = GetColorsForRider(CurrentGoogleId);
                        _myPinVm = new RiderPin(MapPinClicked)
                        {
                            Username = "You",
                            Speed = "0 km/h",
                            Location = currentLocation,
                            PinColor = myColors.PinColor,
                            ZIndex = 100F,
                            ImageSource = "clipart2240358"
                        };
                        MapPins.Add(_myPinVm);
                    }
                });
            }
        }
        catch (Exception) { MainThread.BeginInvokeOnMainThread(() => LocationDisabledOverlay.Show()); }
        finally { GlobalLoadingOverlay.Hide(); }
    }
    private void OnClearPoisClicked(object sender, EventArgs e)
    {
        _poiManager.ClearTemporaryPois();
    }
    // =====================================================================
    // --- NEW: SMOOTH PIN ANIMATION ENGINE ---
    // =====================================================================
    private void AnimatePinMovement(RiderPin pin, Location newLocation, double newHeading, uint durationMs = 1000)
    {
        if (pin.Location == null)
        {
            pin.Location = newLocation;
            pin.Heading = newHeading;
            return;
        }

        double startLat = pin.Location.Latitude;
        double startLng = pin.Location.Longitude;
        double startHeading = pin.Heading;

        // Ensure the rotation takes the shortest path (e.g., from 350° to 10° shouldn't spin all the way backwards)
        double headingDiff = newHeading - startHeading;
        if (headingDiff > 180) startHeading += 360;
        else if (headingDiff < -180) startHeading -= 360;

        var animation = new Animation(t =>
        {
            // Linear Interpolation (Lerp)
            double currentLat = startLat + ((newLocation.Latitude - startLat) * t);
            double currentLng = startLng + ((newLocation.Longitude - startLng) * t);
            double currentHeading = startHeading + ((newHeading - startHeading) * t);

            // Update the UI
            pin.Location = new Location(currentLat, currentLng);
            pin.Heading = currentHeading;
        });

        // The name parameter uniquely identifies the animation.
        // If a new ping comes in, starting a new animation with the same name automatically safely kills the old one!
        animation.Commit(this, $"PinAnim_{pin.Username}", length: durationMs, easing: Easing.Linear);
    }
    // =====================================================================
    // --- NEW: LOCAL MAP SETTINGS MANAGEMENT ---
    // =====================================================================
    private void LoadLocalMapSettings()
    {
        // Init the LiveMap state
        LiveMap.IsTrafficEnabled = Preferences.Default.Get("Map_Traffic", true);
        int mapType = Preferences.Default.Get("Map_Style", (int)Microsoft.Maui.Maps.MapType.Street);
        LiveMap.MapType = (Microsoft.Maui.Maps.MapType)mapType;

        // Init the Compass Button state
        _isHeadingUp = Preferences.Default.Get("Map_HeadingUp", false);
        HeadingUpButton.BackgroundColor = _isHeadingUp ? Colors.DodgerBlue : (Application.Current.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#333333") : Colors.White);
        HeadingUpButton.TextColor = _isHeadingUp ? Colors.White : Colors.DodgerBlue;
    }
    private void OnHeadingUpClicked(object sender, EventArgs e)
    {
        _isHeadingUp = !_isHeadingUp;

        // THE FIX: Save it so the Android Handler can read it instantly
        Preferences.Default.Set("Map_HeadingUp", _isHeadingUp);

        HeadingUpButton.BackgroundColor = _isHeadingUp ? Colors.DodgerBlue : (Application.Current.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#333333") : Colors.White);
        HeadingUpButton.TextColor = _isHeadingUp ? Colors.White : Colors.DodgerBlue;

        // Re-trigger the pin heading event to force the camera to rotate immediately
        if (_myPinVm != null)
        {
            _myPinVm.Heading = _myPinVm.Heading;
        }
    }
    private void UpdateAdminButtonsVisibility()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            bool hasMultipleRiders = Riders.Count(r => r.IsOnline) > 1;

            // Pass the state down to the component to handle!
            DrawerAdminTab.UpdateVisibility(groupDetails?.CurrentState ?? GroupState.NotNavigating, _amIAdmin, hasMultipleRiders);
        });
    }
    private void EvaluateDayNightCycle(Location loc)
    {
        if (loc == null) return;

        // Only run the astronomy math once every 5 minutes to save battery
        if ((DateTime.Now - _lastSolarCheckTime).TotalMinutes < 5) return;
        _lastSolarCheckTime = DateTime.Now;

        bool isNight = SolarEngine.IsNight(loc.Latitude, loc.Longitude);

        if (_isCurrentlyNight == null || isNight != _isCurrentlyNight)
        {
            _isCurrentlyNight = isNight;
#if ANDROID
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (LiveMap.Handler is SpeedyCompass.Platforms.Android.CustomMapHandler handler)
                {
                    handler.UpdateMapTheme(isNight);
                }
            });
#endif
        }
    }
    private void ToggleNavigationPerspective(bool isNavigating)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (LiveMap.Handler is SpeedyCompass.Platforms.Android.CustomMapHandler handler)
            {
                handler.SetNavigationPerspective(isNavigating);
            }
        });
#endif
    }
    private void OnMapStyleChanged(object sender, Microsoft.Maui.Maps.MapType newMapType)
    {
        LiveMap.MapType = newMapType;
    }

    private void OnTrafficToggled(object sender, bool isTrafficEnabled)
    {
        LiveMap.IsTrafficEnabled = isTrafficEnabled;
    }
}