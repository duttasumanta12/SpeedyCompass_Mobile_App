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
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
#if ANDROID
using static Android.Provider.Contacts.Intents;
using BatteryState = Microsoft.Maui.Devices.BatteryState;
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
    private readonly AppTierService _tierService;
    private readonly IPttMeshService? _pttMesh;
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
    private HashSet<string> _ridersAtMeetup = new();

    private bool _isLeavingGroupPermanently = false;

    // --- PTT State ---
    private readonly HardwareButtonService _hwButtonService;
    private string _currentSpeaker = string.Empty;
    private CancellationTokenSource _pttCts;
    private int _pttTimeRemaining;
    private MapNavigationMode _currentNavMode = MapNavigationMode.Immersive;
    private DateTime _lastAutoFrameTime = DateTime.MinValue;

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
    // --- NEW: Quick local tracker for calculating other riders' speeds ---
    private readonly Dictionary<string, DateTime> _riderLastUpdateTimes = new();
    private readonly DeviceCapabilityService _deviceCapabilityService;
    private readonly MapCameraEngine? _mapCameraEngine;
    private bool? _isCurrentlyNight = null;
    private DateTime _lastSolarCheckTime = DateTime.MinValue;
    private readonly Channel<LocalLocationUpdate> _localLocationChannel;
    private readonly Channel<(string RiderId, double Lat, double Lng, double Heading, int BatteryPct)> _networkLocationChannel;
    private readonly SemaphoreSlim _rerouteGate = new(1, 1);
    private CancellationTokenSource _lifecycleCts;
    private readonly object _routeStateLock = new();
    private readonly LobbyStateMachine _stateMachine = new();
    private readonly ILocationBroadcastPolicy _broadcastPolicy;
    private DateTime _lastBroadcastPrefsRefreshUtc = DateTime.MinValue;
    private int _broadcastAggroMode;
    private int _broadcastBatteryThrottle;
    private double _broadcastLocalMinUpdate;
    private double _broadcastLocalMaxUpdate;
    private CancellationTokenSource _crashCts;
    private List<MapElement> _turnOverlayLines = new();
    private WeatherService weatherService;
    private Rider _selectedRiderForManagement;
    private readonly bool _isPttEnabled;
    private Action _announceReroute;
    private Action<string> _announceDeviation;
    private Action<string> _announceTraffic;
    private Action<string> _announceWeather;
    private Action<double> _announceElevation;
    private readonly Queue<int> _turnOverlayElementsPerStep = new();
    private readonly ILogger<LobbyPage> _logger;

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
        RefreshBroadcastPrefsIfNeeded();

        var input = new BroadcastPolicyInput(
            LastBroadcastLocation: _rideCache.LastBroadcastLocation,
            LastNetworkBroadcastTimeUtc: _rideCache.LastNetworkBroadcastTime,
            CurrentLocation: currentLoc,
            SpeedKmh: speedKmh,
            GroupDetails: groupDetails,
            AggressivenessMode: _broadcastAggroMode,
            BatteryThrottlePercentage: _broadcastBatteryThrottle,
            BatteryLevelPercent: Battery.Default.ChargeLevel * 100,
            BatteryState: Battery.Default.State,
            LocalMinUpdateMeters: _broadcastLocalMinUpdate,
            LocalMaxUpdateMeters: _broadcastLocalMaxUpdate);

        return _broadcastPolicy.ShouldBroadcast(input);
    }

    public LobbyPage(SignalRService signalRService,  GroupDetailsDto groupDetails)
    {
        InitializeComponent();
        BindingContext = this;
        // 1. Initialize Bounded Queues
        _localLocationChannel = Channel.CreateBounded<LocalLocationUpdate>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _networkLocationChannel = Channel.CreateBounded<(string, double, double, double, int)>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        LiveMap.NativePoiClicked += OnNativePoiClicked;

        DeviceDisplay.Current.KeepScreenOn = Preferences.Default.Get("Map_KeepScreenOn", false);

        _signalRService = signalRService;
        _logger = IPlatformApplication.Current?.Services.GetService<ILogger<LobbyPage>>();
        _voiceEngine = IPlatformApplication.Current?.Services.GetService<IVoiceCopilotEngine>();
        _rideCache = IPlatformApplication.Current?.Services.GetService<RideStateService>();
        _routingEngine = IPlatformApplication.Current?.Services.GetService<IRoutingEngine>();
        _telemetryEngine = IPlatformApplication.Current?.Services.GetService<ITelemetryEngine>();
        _deviationEngine = IPlatformApplication.Current?.Services.GetService<RouteDeviationEngine>();
        _placeDiscoveryService = IPlatformApplication.Current?.Services.GetService<IPlaceDiscoveryService>();
        _deviceCapabilityService = IPlatformApplication.Current?.Services.GetService<DeviceCapabilityService>();
        _mapCameraEngine = IPlatformApplication.Current?.Services.GetService<MapCameraEngine>();
        weatherService = IPlatformApplication.Current?.Services.GetService<WeatherService>();
        _tierService = IPlatformApplication.Current.Services.GetService<AppTierService>();

        if (_tierService == null)
        {
            _tierService = new AppTierService(); // safe fallback
        }

        _pttMesh = IPlatformApplication.Current.Services.GetRequiredService<IPttMeshService>();
        _isPttEnabled = _tierService.UsePttVoice;

        if (_isPttEnabled)
        {
            _pttMesh.InitializeSession(groupDetails.GroupName, CurrentGoogleId);
            DrawerActionsTab.SetPttVisible(_isPttEnabled);
            _pttMesh.AudioLevelsUpdated += OnPttAudioLevelsUpdated;

            PttOverlay.CloseRequested += OnPttOverlayCloseRequested;
            DrawerActionsTab.PttClicked += OnHardwarePttPressed;

            _signalRService.PttDenied += OnPttDenied;
            _signalRService.PttReleased += OnPttReleased;

            _hwButtonService = IPlatformApplication.Current?.Services.GetService<HardwareButtonService>();
            if (_hwButtonService != null)
            {
                _hwButtonService.PttPressed += OnHardwarePttPressed;
                _hwButtonService.PttReleased += OnHardwarePttReleased;
            }
        }
        if (!_tierService.IsProTierEnabled) _currentNavMode = MapNavigationMode.BackgroundSharing;

        ConfigureAlertPipelines();

        _simulatorService = IPlatformApplication.Current?.Services.GetService<RideSimulatorService>();
        InitializeCoordinators();
        _poiManager = new MapPoiManager(LiveMap, _rideCache, _placeDiscoveryService);
        _broadcastPolicy = IPlatformApplication.Current?.Services.GetService<ILocationBroadcastPolicy>()!;

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
            // =====================================================================
            // THE FIX: Wire up the mock crash event!
            // =====================================================================
            _simulatorService.OnSimulatedCrash = () =>
            {
                TriggerCrashProtocol(); // Fires the 10-second UI emergency countdown
            };
        }

        this.groupDetails = groupDetails;
        _stateMachine.Initialize(GroupState.NotNavigating);
        _stateMachine.StateChanged += OnStateTransitioned;

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

        RosterControl.SetRidersSource(Riders);

        DrawerMapSettingsTab.NavigationModeChanged += OnNavigationModeChanged;
        var preferredNavMode = (MapNavigationMode)Preferences.Default.Get("Map_NavigationMode", (int)MapNavigationMode.Immersive);
        _currentNavMode = _tierService.IsProTierEnabled ? preferredNavMode : MapNavigationMode.BackgroundSharing;
        Preferences.Default.Set("Map_NavigationMode", (int)_currentNavMode); // keep persisted state compliant

        DrawerMapSettingsTab.ApplyTierPolicy(_tierService.IsProTierEnabled);

        SensoryAlertOverlay.CrashCancelled += OnCrashCancelledClicked;
        SensoryAlertOverlay.CrashEmergencyConfirmed += OnCrashEmergencyClicked;

        InitializeSignalRBindings();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        string flowId = CorrelationContext.Current ?? CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] LobbyPage OnAppearing triggered for Group: {GroupName}", flowId, GroupNameLabel.Text);

        if (_lifecycleCts == null || _lifecycleCts.IsCancellationRequested)
        {
            _lifecycleCts = new CancellationTokenSource();
            _ = ProcessLocalLocationsAsync(_lifecycleCts.Token);
            _ = ProcessNetworkLocationsAsync(_lifecycleCts.Token);
        }
        if (_hasJoined) return;

        try
        {
            GlobalLoadingOverlay.Show("Syncing Convoy State...");
            _logger?.LogInformation("[{FlowId}] Fetching fresh group details from backend.", flowId);

            // 1. THE FIX: Force a fresh, synchronous fetch of the Group Details from the server RIGHT NOW.
            // This eliminates the stale constructor `groupDetails` race condition!
            var freshDetails = await _signalRService.GetGroupDetails(GroupNameLabel.Text);
            if (freshDetails != null)
            {
                this.groupDetails = freshDetails;

                if (_rideCache.CurrentSettings?.GroupName != freshDetails.Settings?.GroupName)
                {
                    _rideCache.HardResetAll();
                }
                _rideCache.CurrentSettings = freshDetails.Settings;
            }

            // 2. Safely load our local DB (Odometer, Settings, Cached Polyline)
            await _rideCache.LoadSnapshotAsync();

            var roster = await _signalRService.GetGroupRoster(GroupNameLabel.Text);
            if (roster != null) OnRosterUpdated(roster);

            _hasJoined = true;
            await InitializeLocalTrackingAsync();

            OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty);

            if (this.groupDetails != null)
            {
                ConvoyPin = this.groupDetails.JoinCode ?? "------";
                RosterControl.SetConvoyPin(ConvoyPin);
                RosterControl.SetAdminPinCardVisible(_amIAdmin);

                GroupState serverState = this.groupDetails.CurrentState;

                // THE FIX: Prevent passing 0,0 if the server state is Navigating
                if (serverState == GroupState.Navigating && this.groupDetails.DestLat != 0)
                {
                    OnNavigationStarted(this.groupDetails.DestLat, this.groupDetails.DestLng, this.groupDetails.DestName, isSyncRequired: true);
                }
                else if (serverState >= GroupState.PausedBreak && serverState <= GroupState.PausedMechanical && this.groupDetails.DestLat != 0)
                {
                    await RestorePausedStateSilentlyAsync(serverState);
                }
                else if (serverState == GroupState.DestinationSet && this.groupDetails.DestLat != 0)
                {
                    await ChangeGroupState(serverState, forceSync: true);
                }
                else
                {
                    await ChangeGroupState(serverState, forceSync: true);
                }
            }

            // ONLY fire the UI connection status AFTER we've settled the state, 
            // so we don't trigger a secondary race condition in OnConnectionStatusChanged.
            OnConnectionStatusChanged("Connected", Colors.MediumSeaGreen);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[{FlowId}] Fatal error loading lobby.", flowId);
            await DisplayAlertAsync("Error", $"Could not load lobby: {ex.Message}", "OK");
            await ClosePageAsync();
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
            _logger?.LogInformation("[{FlowId}] LobbyPage OnAppearing complete.", flowId);
        }
    }
    private void OnNavigationModeChanged(object sender, MapNavigationMode mode)
    {
        if (!_tierService.IsProTierEnabled && mode == MapNavigationMode.Immersive)
        {
            mode = MapNavigationMode.BackgroundSharing;
            Preferences.Default.Set("Map_NavigationMode", (int)mode);
        }

        _currentNavMode = mode;
        AppLogger.Info("Navigation", $"Switched to {mode} mode.");

        if (mode == MapNavigationMode.BackgroundSharing)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OnOverviewClicked(null, EventArgs.Empty);
                NextTurnOverlay.IsVisible = false;

                foreach (var overlay in _turnOverlayLines)
                    LiveMap.MapElements.Remove(overlay);

                _turnOverlayLines.Clear();
                _turnOverlayElementsPerStep.Clear();
            });

            _signalRService.ToggleBackgroundListenerMode(GroupNameLabel.Text, true).SafeFireAndForget();
        }
        else
        {
            _ = _signalRService.ToggleBackgroundListenerMode(GroupNameLabel.Text, false);

            if (groupDetails?.CurrentState >= GroupState.Navigating && _rideCache.ActiveDestination != null)
            {
                MainThread.BeginInvokeOnMainThread(() => OnMapFollowClicked(null, EventArgs.Empty));

                // Plug-and-play activation from cached state, no route recalculation
                ActivateImmersiveGuidanceFromCacheAsync()
                    .SafeFireAndForget(ex => AppLogger.Error("Navigation", ex, "Failed to activate immersive guidance from cache."));
            }
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

        ToggleCrashDetection(false);

        _lifecycleCts?.Cancel();
        _rideCts?.Cancel();
        _pttCts?.Cancel();

        _rideCache.SaveSnapshotAsync().SafeFireAndForget();

        DisposeSignalRBindings();
        if (_isPttEnabled)
        {
            PttOverlay.CloseRequested -= OnPttOverlayCloseRequested;

            if (_hwButtonService != null)
            {
                _hwButtonService.PttPressed -= OnHardwarePttPressed;
                _hwButtonService.PttReleased -= OnHardwarePttReleased;
            }

            if (_pttMesh != null)
            {
                _pttMesh.AudioLevelsUpdated -= OnPttAudioLevelsUpdated;
                _pttMesh.StopSession();
            }
            _signalRService.PttDenied -= OnPttDenied;
            _signalRService.PttReleased -= OnPttReleased;
        }

        if (_locationTracker != null)
        {
            _locationTracker.LocationUpdated -= OnLocalLocationPushedFromBackground;
        }
        if (_simulatorService != null)
        {
            _simulatorService.OnLocationGenerated = null;

            // =====================================================================
            // THE FIX 2: Release the Singleton's grip on this page!
            // =====================================================================
            _simulatorService.OnSimulatedCrash = null;
        }

        _simulatorService?.StopSimulation();
        _locationTracker?.StopTracking();
        DisposeCoordinators();
#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif

        if (!_isLeavingGroupPermanently)
        {
            //_ = _signalRService.LeaveLobby();
        }
        Task.Run(async () => await _signalRService.StopAsync()).SafeFireAndForget();
        BindingContext = null;
    }
    private async void OnNativePoiClicked(object sender, PoiClickedEventArgs e)
    {
        if (!_amIAdmin || groupDetails?.CurrentState == GroupState.Navigating) return;

        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Admin tapped POI: {PoiName}", flowId, e.Name);

        try
        {
            LiveMap.MapElements.Clear();
            LiveMap.Pins.Clear();

            _pendingDestination = e.Location;
            string destName = e.Name;

            try
            {
                var placemarks = await Geocoding.Default.GetPlacemarksAsync(e.Location.Latitude, e.Location.Longitude);
                var placemark = placemarks?.FirstOrDefault();
                if (placemark != null)
                {
                    destName = $"{placemark.FeatureName} {placemark.Thoroughfare}, {placemark.Locality}".Trim(' ', ',');
                }
            }
            catch (Exception geoEx) { _logger?.LogWarning(geoEx, "[{FlowId}] POI reverse geocoding failed.", flowId); }

            DestinationSearchControl.InjectExternalSelection(destName, e.Location);
            UpdateDestinationPin(_pendingDestination, destName);

            var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
            if (currentLoc != null)
            {
                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _pendingDestination]));
            }

            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(50));
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "POI Click Processing", flowId);
        }
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
        MainThread.BeginInvokeOnMainThread(async () =>
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

                bool isEssential = IsEssentialRole(r.Role, r.IsAdmin);

                if (r.GoogleId != CurrentGoogleId)
                {
                    // First time seeing them: default to visible
                    if (!_rideCache.VisibilityInitialized.Contains(r.Name))
                    {
                        _rideCache.VisibilityInitialized.Add(r.Name);

                        if (_rideCache.HiddenRiders.Remove(r.Name))
                        {
                            _signalRService.SendVisibilityToggle(GroupNameLabel.Text, r.Name, false).SafeFireAndForget();
                        }
                    }
                    // Promoted to an essential role: force them visible
                    else if (isEssential && _rideCache.HiddenRiders.Contains(r.Name))
                    {
                        _rideCache.HiddenRiders.Remove(r.Name);
                        _signalRService.SendVisibilityToggle(GroupNameLabel.Text, r.Name, false).SafeFireAndForget();
                    }
                }

                if (_rideCache.HiddenRiders.Contains(r.Name)) displayName += " (Hidden)";

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
            RosterControl.SetRidersSource(Riders);
            var onlineRiderCount = roster.Count(r => r.IsOnline);
            DrawerActionsTab.SetPttEnabled(_isPttEnabled && onlineRiderCount > 1);

            RosterControl.SetAdminPinCardVisible(_amIAdmin);
            TabAdminBtn.IsVisible = _amIAdmin;

            _locationTracker?.UpdateRiderCount(Riders.Count(r => r.IsOnline));
            UpdateAdminButtonsVisibility();
            if (_pttMesh != null)
            {
                await _pttMesh.SyncMeshNetworkAsync(roster);
            }
        });
    }
    private async Task TrimRouteVisuals(Location currentLocation)
    {
        if (_activeRouteLine == null || _rideCache.ActiveDestination == null || _rideCache.CurrentRoutePoints.Count < 2) return;
        if (_rideCts == null || _rideCts.IsCancellationRequested) return;

        try
        {
            bool voiceEnabled = Preferences.Default.Get("Map_VoiceNav", true);

            List<RouteStep> stepsSnapshot;
            DateTime rerouteTimeBeforeMath;
            int stepsBeforeMath;

            lock (_routeStateLock)
            {
                // Normalize stale completed steps first (voice engine may have flagged them previously)
                int rawCount = _activeRouteSteps.Count;
                stepsSnapshot = _activeRouteSteps.Where(s => !s.StepCompleted).ToList();
                int preTrimmed = rawCount - stepsSnapshot.Count;

                if (preTrimmed > 0)
                {
                    TrimConsumedTurnOverlays(preTrimmed);
                }

                stepsBeforeMath = stepsSnapshot.Count;
                rerouteTimeBeforeMath = _rideCache.LastRerouteTime;
            }

            var telemetry = await _routingEngine.ProcessRouteTelemetryAsync(
                currentLocation, _rideCache, _deviationEngine, stepsSnapshot,
                _rideCache.HasAnnouncedArrival, _rideCache.LastAnnouncedTurn, voiceEnabled, _rideCts.Token);

            int consumedByTelemetry = Math.Max(0, stepsBeforeMath - stepsSnapshot.Count);
            TrimConsumedTurnOverlays(consumedByTelemetry);

            _rideCache.CurrentRouteIndex = telemetry.NewRouteIndex;
            _rideCache.LastOdometerLocation = currentLocation;
            _rideCache.HasAnnouncedArrival = telemetry.UpdatedHasAnnouncedArrival;
            _rideCache.LastAnnouncedTurn = telemetry.UpdatedLastAnnouncedTurn;

            if (telemetry.SpeakDestinationReached)
                MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak("You have arrived at your destination."));

            if (telemetry.SpeakTrafficAlert && !string.IsNullOrWhiteSpace(telemetry.TrafficAlertMessage))
            {
                _announceTraffic?.Invoke(telemetry.TrafficAlertMessage);
            }

            if (_currentNavMode == MapNavigationMode.Immersive &&
                groupDetails?.CurrentState == GroupState.Navigating &&
                voiceEnabled)
            {
                _voiceEngine.ProcessTurnByTurn(currentLocation, stepsSnapshot);

                // Voice engine flags StepCompleted; physically remove them so UI + telemetry stay aligned
                int beforeVoicePrune = stepsSnapshot.Count;
                stepsSnapshot.RemoveAll(s => s.StepCompleted);
                int consumedByVoice = beforeVoicePrune - stepsSnapshot.Count;
                if (consumedByVoice > 0)
                {
                    TrimConsumedTurnOverlays(consumedByVoice);
                }
            }

            lock (_routeStateLock)
            {
                if (_rideCache.LastRerouteTime == rerouteTimeBeforeMath)
                {
                    _activeRouteSteps.Clear();
                    _activeRouteSteps.AddRange(stepsSnapshot);
                }
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_rideCts.IsCancellationRequested) return;

                TelemetryHeaderControl.UpdateTelemetryStats(
                    distText: telemetry.IsOffRoute ? (telemetry.UserMessage ?? "Rerouting...") : telemetry.DistLeftStr,
                    distColor: telemetry.IsOffRoute ? telemetry.AlertColor : Colors.DodgerBlue,
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

            // 5. REROUTING LOGIC WITH SEMAPHORE GATE
            if (_tierService.IsProTierEnabled && telemetry.ShouldReroute)
            {
                _rideCache.LastRerouteTime = DateTime.Now;

                if (await _rerouteGate.WaitAsync(0))
                {
                    Task.Run(async () =>
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
                        finally
                        {
                            _rerouteGate.Release();
                        }
                    }, _rideCts.Token).SafeFireAndForget(ex => AppLogger.Error("Routing", ex, "Failed to recalculate."));

                    // in TrimRouteVisuals() reroute section
                    _announceReroute?.Invoke();
                }
            }

            double currentSpeedKmh = (currentLocation.Speed ?? 0) * 3.6;
            await _telemetryEngine.EvaluateEdgeTelemetryAsync(
                currentLocation,
                currentSpeedKmh,
                _myName,
                GroupNameLabel.Text,
                _amIAdmin,
                telemetry.DistLeftKm);
        }
        catch (OperationCanceledException) { }
    }
    // --- NEW: Close Button Handler ---
    private async void OnCloseRideSummaryClicked(object sender, EventArgs e)
    {
        RideSummaryOverlay.IsVisible = false;

        // THE FIX: Return the app to the idle Lobby state so the
        // Search Bar and other lobby controls fully unlock again!
        ChangeGroupState(GroupState.NotNavigating, forceSync: true).SafeFireAndForget();
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
    // --- NEW VOICE NAV VARIABLES ---
    private List<RouteStep> _activeRouteSteps = new();

    private async void OnLeadRouteUpdated(string encodedPolyline)
    {
        if (!_tierService.IsProTierEnabled) return;

        MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak("Lead rider has updated the route. Syncing map."));

        var leadRoutePoints = _routingEngine.DecodeGooglePolyline(encodedPolyline);
        if (leadRoutePoints == null || leadRoutePoints.Count == 0) return;

        var detourStart = leadRoutePoints.First();
        var combinedPoints = new List<Location>();

        int seamIndex = -1;
        double minDistance = double.MaxValue;

        if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Count > 0)
        {
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

        if (seamIndex != -1 && minDistance < 1.0)
        {
            AppLogger.Info("Routing", $"Found route seam at index {seamIndex} ({Math.Round(minDistance * 1000)}m gap). Splicing detour...");

            // include seam point to avoid tiny visual gap at merge junction
            var historySlice = _rideCache.CurrentRoutePoints.Take(seamIndex + 1).ToList();
            combinedPoints.AddRange(historySlice);
            combinedPoints.AddRange(leadRoutePoints);
        }
        else
        {
            AppLogger.Info("Routing", "Seam too far or not found. Stitching catch-up gap.");
            var currentLoc = _rideCache.LastOdometerLocation ?? _lastKnownLocation;

            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRouteIndex > 0)
            {
                combinedPoints.AddRange(_rideCache.CurrentRoutePoints.Take(_rideCache.CurrentRouteIndex + 1));
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

        _rideCache.CurrentRoutePoints = combinedPoints;
        _rideCache.OffRouteStrikeCount = 0;
        _rideCache.LastRerouteTime = DateTime.Now;
        _rideCache.CurrentTrafficData.Clear();

        lock (_routeStateLock)
        {
            // lead route update payload has no step metadata, so clear stale instructions
            _activeRouteSteps.Clear();
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // replace only main active route, keep other overlays intact
            if (_activeRouteLine != null)
                LiveMap.MapElements.Remove(_activeRouteLine);

            _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 22f };
            foreach (var coord in _rideCache.CurrentRoutePoints)
                _activeRouteLine.Geopath.Add(coord);

            LiveMap.MapElements.Add(_activeRouteLine);
        });
    }

    private void OnRouteDeviationAlert(string userName)
    {
        MainThread.BeginInvokeOnMainThread(() => _announceDeviation?.Invoke(userName));
    }
    private async Task GenerateMeetupPointAsync()
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.OtherRiderLocations.Count == 0 || _rideCache.ActiveDestination == null) return;

        string flowId = CorrelationContext.Current ?? CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Initiating Meetup Point calculation. Active riders: {Count}", flowId, _rideCache.OtherRiderLocations.Count);

        MainThread.BeginInvokeOnMainThread(() => GlobalLoadingOverlay.Show("Calculating Convergence..."));
        _voiceEngine.Speak("Calculating a safe meetup point for the group. Please wait.");

        try
        {
            Location meetupPoint;
            if (_tierService.UseAlgorithmicMeetups)
            {
                _logger?.LogInformation("[{FlowId}] Calling Pro Dynamic Routing Engine...", flowId);
                meetupPoint = await _routingEngine.CalculateDynamicMeetupPointAsync();
            }
            else
            {
                _logger?.LogInformation("[{FlowId}] Calculating Free Tier Center-of-Mass...", flowId);
                meetupPoint = _routingEngine.CalculateCenterOfMassMeetup(_rideCache.OtherRiderLocations.Values.ToList());
            }

            if (meetupPoint == null)
            {
                _logger?.LogInformation("[{FlowId}] Engine returned null (Riders are safe). Clearing meetup pin.", flowId);
                await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, 0, 0);
            }
            else
            {
                _logger?.LogInformation("[{FlowId}] Meetup point calculated at {Lat}, {Lng}. Broadcasting.", flowId, meetupPoint.Latitude, meetupPoint.Longitude);
                await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, meetupPoint.Latitude, meetupPoint.Longitude);
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Generate Meetup Point", flowId);
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() => GlobalLoadingOverlay.Hide());
        }
    }
    private void OnMeetupPointSet(double lat, double lng)
    {
        _rideCache.HaveIReachedMeetup = false;
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

                if (_tierService.UseStraightLineSpiderwebs)
                {
                    // FREE TIER: Just draw a straight line
                    var webLine = _routingEngine.CreateStraightLineSpiderweb(rider.Value, _rideCache.ActiveMeetupPoint, colorProfile.RouteColor);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        LiveMap.MapElements.Add(webLine);
                        _otherRiderRoutes.Add(webLine);
                    });
                }
                else
                {

                    // Spiderweb strictly from the Rider -> Meetup Point
                    CalculateAndDrawRoute(
                    origin: rider.Value,
                    dest: _rideCache.ActiveMeetupPoint,
                    meetup: null,
                    routeColor: colorProfile.RouteColor,
                    riderName: rider.Key,
                    isMainRoute: false).SafeFireAndForget();
                }
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

    // 🔄 REPLACE entire method in LobbyPage.xaml.cs
    private void OnDrawerTabClicked(object sender, EventArgs e)
    {
        // 1. Reset all tabs to default state
        var buttons = new[] { TabActionsBtn, TabStatsBtn, TabMapSettingsBtn, TabAdminBtn };
        var views = new View[] { DrawerActionsTab, ConvoyTabContainer, DrawerMapSettingsTab, DrawerAdminTab };

        foreach (var btn in buttons)
        {
            btn.BackgroundColor = Colors.Transparent;
            btn.TextColor = Colors.Gray;
        }
        foreach (var view in views) view.IsVisible = false;

        // 2. Determine which tab was actually activated
        Button activeBtn = TabStatsBtn; // Default
        View activeView = ConvoyTabContainer;

        if (sender == TabActionsBtn && groupDetails.CurrentState >= GroupState.Navigating)
        {
            activeBtn = TabActionsBtn; activeView = DrawerActionsTab;
        }
        else if (sender == TabAdminBtn && _amIAdmin)
        {
            activeBtn = TabAdminBtn; activeView = DrawerAdminTab;
        }
        else if (sender == TabMapSettingsBtn)
        {
            activeBtn = TabMapSettingsBtn; activeView = DrawerMapSettingsTab;
        }

        // 3. Highlight the active tab
        activeBtn.BackgroundColor = Colors.DodgerBlue;
        activeBtn.TextColor = Colors.White;
        activeView.IsVisible = true;

        // 4. Snap drawer up if it's too low
        if (ActionDrawer.TranslationY >= (_drawerFullHeight - _drawerPeekHeight) - 10)
            ActionDrawer.TranslateTo(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
    }
    // --- STATE MACHINE --
    private Task ChangeGroupState(GroupState newState, string triggerUser = "", string reason = "", bool forceSync = false)
    {
        if (!_amIAdmin && groupDetails != null && groupDetails.CurrentState == GroupState.Navigating)
        {
            bool isPausingOrCompleting = newState >= GroupState.PausedBreak && newState <= GroupState.Completed;

            if (isPausingOrCompleting && !forceSync)
            {
                // THE FIX: If we don't know where the admin is, fall back to the Destination Pin!
                var targetLoc = GetAdminLocation() ?? _rideCache.ActiveDestination;

                if (targetLoc != null && _lastKnownLocation != null)
                {
                    double distToTargetKm = Location.CalculateDistance(_lastKnownLocation, targetLoc, DistanceUnits.Kilometers);

                    if (distToTargetKm > 0.5)
                    {
                        AppLogger.Info("CatchUp", $"Intercepted {newState}. Rider is {Math.Round(distToTargetKm, 1)}km away.");

                        _rideCache.PendingCatchUpState = newState;

                        string action = newState == GroupState.Completed ? "completed the route" : "paused the ride";
                        _voiceEngine.Speak($"The admin has {action} ahead of you. Keep riding to catch up.");

                        return Task.CompletedTask;
                    }
                }
            }
        }

        _rideCache.PendingCatchUpState = null;
        _stateMachine.TryTransition(newState, triggerUser, reason, forceSync);
        return Task.CompletedTask;
    }
    private Location GetAdminLocation()
    {
        if (groupDetails == null || string.IsNullOrEmpty(groupDetails.AdminGoogleId)) return null;

        // 1. Identify the Admin from the Roster
        var adminRider = Riders.FirstOrDefault(r => r.GoogleId == groupDetails.AdminGoogleId);
        if (adminRider == null) return null;

        // Remove UI tags to match the raw dictionary key
        string rawName = adminRider.Name.Replace(" (Offline)", "").Replace(" (You)", "");

        // 2. Grab their exact coordinates from the live telemetry cache
        if (_rideCache.OtherRiderLocations.TryGetValue(rawName, out var loc))
            return loc;

        if (_riderViewModels.TryGetValue(rawName, out var vm))
            return vm.Location;

        return null;
    }
    // =====================================================================
    // UI STATE RENDERER LAYER
    // =====================================================================
    private void OnStateTransitioned(object sender, StateTransitionEventArgs e)
    {
        // 1. Keep our local models in sync
        this.groupDetails.CurrentState = e.NewState;
        _stateStartTime = DateTime.Now;

        // 2. Safely push all visual changes to the UI Thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (e.NewState)
            {
                case GroupState.DestinationSet: RenderDestinationSetState(); break;
                case GroupState.NotNavigating: RenderIdleState(e); break;
                case GroupState.Completed: RenderCompletedState(e); break;
                case GroupState.Navigating: RenderNavigatingState(e); break;
                case GroupState.PausedBreak:
                case GroupState.PausedHazard:
                case GroupState.PausedMechanical: RenderPausedState(e); break;
            }

            UpdateAdminButtonsVisibility();
        });

        // 3. Snapshot the new state to the SQLite/Disk Outbox
        _rideCache.SaveSnapshotAsync().SafeFireAndForget();
    }
    private async void RenderCompletedState(StateTransitionEventArgs e)
    {
        // 1. Process Telemetry FIRST (before we clear the local cache)
        string currentGroupName = GroupNameLabel.Text;
        var finalSummary = await _telemetryEngine.ProcessAndSaveRideTelemetryAsync(currentGroupName);

        // 2. Perform the standard map wipe (Idle teardown)
        RenderIdleState(e);

        // 3. Pop the Ride Summary Overlay!
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (finalSummary != null)
            {
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
    }

    private void RenderDestinationSetState()
    {
        OnDestinationSet(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
        DestinationSearchControl.SetDestinationText(groupDetails.DestName);

        IdleHeader.IsVisible = false;
        PreNavigationHeader.IsVisible = true;
        TelemetryHeaderControl.IsVisible = false;

        DestinationSearchControl.SetState(isVisible: _amIAdmin, isReadOnly: true, showBanner: false, showConfirm: false);

        ActionDrawer.IsVisible = true;
        ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;
        FloatingMapControls.IsVisible = true;
    }

    private void RenderIdleState(StateTransitionEventArgs e)
    {
        ToggleCrashDetection(false);
        IdleHeader.IsVisible = true;
        PreNavigationHeader.IsVisible = false;
        TelemetryHeaderControl.IsVisible = false;

        AdminIdleHeader.IsVisible = _amIAdmin;
        RiderIdleHeader.IsVisible = !_amIAdmin;

        ActionDrawer.IsVisible = true;
        ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;
        FloatingMapControls.IsVisible = false;

        DestinationSearchControl.Reset();
        DestinationSearchControl.SetState(isVisible: _amIAdmin, isReadOnly: false, showBanner: _amIAdmin, showConfirm: true);

        // Map Cleanup
        NextTurnOverlay.IsVisible = false;
        LiveMap.MapElements.Clear();
        LiveMap.Pins.Clear();
        _activeRouteLine = null;
        lock (_routeStateLock) { _activeRouteSteps.Clear(); }
        ClearOtherRiderRoutes();
        _poiManager.ClearTemporaryPois();
        DrawMapBubbles(new List<MapBubble>());

        _locationTracker?.StopTracking();
        _simulatorService.StopSimulation();

        FitMapToBounds();
        ToggleNavigationPerspective(false);

#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif

        if (e.NewState == GroupState.Completed && !string.IsNullOrEmpty(e.TriggerUser))
        {
            _voiceEngine.Speak($"Navigation completed by {e.TriggerUser}. Great ride!");
        }
    }

    private void RenderNavigatingState(StateTransitionEventArgs e)
    {
        ToggleCrashDetection(true);
        IdleHeader.IsVisible = false;
        PreNavigationHeader.IsVisible = false;
        TelemetryHeaderControl.IsVisible = true;

        OnDrawerTabClicked(TabActionsBtn, EventArgs.Empty);

        double maxTranslation = _drawerFullHeight - _drawerPeekHeight;
        _ = ActionDrawer.TranslateToAsync(0, maxTranslation, 250, Easing.CubicOut);

        DestinationSearchControl.SetState(isVisible: false, isReadOnly: false, showBanner: false, showConfirm: false);
        FloatingMapControls.IsVisible = true;
        TabAdminBtn.IsVisible = _amIAdmin;

        TelemetryHeaderControl.SetDestinationName(groupDetails.DestName);
        SetActionButtonsEnabled(true);
        _locationTracker?.StartTracking(GroupNameLabel.Text, Riders.Count(x => x.IsOnline));

#if ANDROID
        MainActivity.IsInNavigationMode = true;
#endif

        if (string.IsNullOrEmpty(e.TriggerUser) && e.OldState < GroupState.Navigating)
        {
            _voiceEngine.Speak("Navigation active. Ride safe!");
        }
    }

    private void RenderPausedState(StateTransitionEventArgs e)
    {
        ToggleCrashDetection(false);
        _simulatorService.StopSimulation();
        _locationTracker?.StopTracking();
        SetActionButtonsEnabled(false);

        string context = e.NewState == GroupState.PausedBreak ? "for a break" :
                         e.NewState == GroupState.PausedHazard ? "due to a hazard" :
                         e.NewState == GroupState.PausedMechanical ? "for mechanical repairs" :
                         "waiting for riders";

        string spokenReason = string.IsNullOrEmpty(e.Reason) ? context : e.Reason;

        if (!string.IsNullOrEmpty(e.TriggerUser))
            _voiceEngine.Speak($"Navigation paused by {e.TriggerUser} {spokenReason}. Tracking suspended.");

        _ = ActionDrawer.TranslateToAsync(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
    }

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
            UpdateWaypointPins(_rideCache.ActiveWaypoints, _rideCache.ActiveDestination);

            var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
            if (currentLoc != null)
            {
                // This updates PreNavDistLabel with the exact distance & ETA
                await CalculateAndDrawRoute(currentLoc, _rideCache.ActiveDestination, isPreview: true);
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
            _ = _rideCache.SaveSnapshotAsync();
        }
        finally { GlobalLoadingOverlay.Hide(); }
    }

    private async void OnNavigationStarted(double destLat, double destLng, string destName, bool isSyncRequired = false)
    {
        bool alreadyNavigating = this.groupDetails?.CurrentState >= GroupState.Navigating && this.groupDetails?.CurrentState < GroupState.Completed;

        bool sameDestination = _rideCache.ActiveDestination != null &&
                               Math.Abs(_rideCache.ActiveDestination.Latitude - destLat) < 0.0001 &&
                               Math.Abs(_rideCache.ActiveDestination.Longitude - destLng) < 0.0001;

        // Always protect telemetry if route is already active to same destination
        if (alreadyNavigating && sameDestination && !isSyncRequired)
        {
            AppLogger.Info("Navigation", "Ignored redundant Start command to protect active telemetry.");
            return;
        }

        // Only wipe the telemetry odometer if this is a BRAND NEW ride
        if (!isSyncRequired)
        {
            AppLogger.ResetRideCorrelationId();
            _rideCts?.Cancel();
            _rideCts = new CancellationTokenSource();

            _rideCache.ResetTelemetryState();
            _rideCache.HasAnnouncedArrival = false;
        }
        else if (_rideCts == null || _rideCts.IsCancellationRequested)
        {
            // Ensure the cancellation token is alive for background tracking
            _rideCts = new CancellationTokenSource();
        }

        AppLogger.Info("Navigation", $"Starting route to {destName}...");

        _rideCache.ActiveDestination = new Location(destLat, destLng);
        _rideCache.ActiveDestinationName = destName;
        DestinationSearchControl.SetDestinationText(destName);

        _rideCache.ResetTelemetryState();
        _rideCache.HasAnnouncedArrival = false;

        await ChangeGroupState(GroupState.Navigating, _myName, forceSync: true);

        Location loc2;
#if DEBUG
        loc2 = _lastKnownLocation ?? await Geolocation.Default.GetLastKnownLocationAsync();
#else
    loc2 = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
#endif

        if (loc2 != null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                TelemetryHeaderControl.SetOriginCoordinates(loc2.Latitude, loc2.Longitude);
            });

            await _signalRService.UpdateLocation(groupName: GroupNameLabel.Text, userName: _myName, lat: loc2.Latitude, lng: loc2.Longitude, 0, -1, new List<string>());

            bool canReusePreviewRoute =
                sameDestination &&
                _rideCache.CachedMainRouteData != null &&
                _rideCache.CachedMainRouteData.DecodedPoints != null &&
                _rideCache.CachedMainRouteData.DecodedPoints.Count > 1;

            // Rebuild visuals from cached route first; fallback to network only if needed.
            string encoded = await CalculateAndDrawRoute(
                loc2,
                _rideCache.ActiveDestination,
                isMainRoute: true,
                isReroute: false,
                allowNetworkFetch: !canReusePreviewRoute);

            if (string.IsNullOrEmpty(encoded))
            {
                await CalculateAndDrawRoute(loc2, _rideCache.ActiveDestination, isMainRoute: true, isReroute: false, allowNetworkFetch: true);
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_currentNavMode == MapNavigationMode.Immersive)
                {
                    if (_myPinVm != null) _myPinVm.IsAutoCentering = true;
                    FitMapToBounds();
                    ToggleNavigationPerspective(true);
                }
                else
                {
                    NextTurnOverlay.IsVisible = false;

                    if (_myPinVm != null) _myPinVm.IsAutoCentering = false;

                    OverviewButton.IsVisible = false;
                    MapFollowButton.IsVisible = true;

                    ToggleNavigationPerspective(false);
                    FitMapToBounds();
                }
            });

#if DEBUG
            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any())
            {
                _= _simulatorService?.StartSimulationAsync(() => groupDetails.CurrentState, _rideCts.Token, RideScenario.Baseline_Navigate_Clean);
            }
            await Task.Delay(3000); // Give the simulator a moment to start before we speak
#endif
        }

        if (!isSyncRequired)
            _voiceEngine.Speak($"Navigation started to {destName}. Ride safe!");

        weatherService.StartRadarLoopAsync(
                                            getCurrentLocation: () => _lastKnownLocation,
                                            rideCache: _rideCache,
                                            routingEngine: _routingEngine,
                                            onBadWeatherDetected: (alert) =>
                                            {
                                                _announceWeather?.Invoke(alert.WarningMessage);

                                                MainThread.BeginInvokeOnMainThread(() =>
                                                {
                                                    SensoryAlertOverlay.TriggerAlertAsync("Weather", alert.IconEmoji, alert.WarningMessage, Color.Parse(alert.AlertColor)).SafeFireAndForget();
                                                });
                                            },
                                            cancelToken: _rideCts.Token).SafeFireAndForget();
    }

    private async void OnNavigationCompleted(string adminName)
    {
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
        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Admin resetting destination.", flowId);

        try
        {
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
                _riderViewModels.Clear();
                var otherRiderPins = MapPins.ToList().Where(x => x.Username != "You");
                foreach (var p in otherRiderPins) MapPins.Remove(p);
            }

            OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty);

            await ChangeGroupState(GroupState.NotNavigating, _myName);
            await _signalRService.CancelGroupNavigation(GroupNameLabel.Text);

            _logger?.LogInformation("[{FlowId}] Destination reset successfully.", flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Reset Destination", flowId);
        }
    }

    // --- REMAINING UTILITIES ---
    private async void OnStartJourneyClicked(object sender, EventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Start Journey clicked.", flowId);
        GlobalLoadingOverlay.Show("Starting Navigation...");

        try
        {
            StartJourneyButton.IsEnabled = false;
            await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _rideCache.ActiveDestination.Latitude, _rideCache.ActiveDestination.Longitude, groupDetails.DestName);
            _logger?.LogInformation("[{FlowId}] Start Navigation command sent successfully.", flowId);
        }
        catch (Exception ex)
        {
            StartJourneyButton.IsEnabled = true;
            await HandleExceptionAsync(ex, "Start Journey", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
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
        if (_lifecycleCts == null || _lifecycleCts.IsCancellationRequested) return;

        // THE FIX: We must extract the locations safely on the Main Thread 
        // to create an immutable snapshot before handing it to the background!
        if (points == null)
        {
            if (MapPins.Count == 0) return;
            if (MainThread.IsMainThread)
            {
                RunMapMath(MapPins.Select(p => p.Location).ToList());
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    RunMapMath(MapPins.Select(p => p.Location).ToList());
                });
            }
        }
        else
        {
            RunMapMath(points);
        }
    }

    private void RunMapMath(List<Location> snapshotPoints)
    {
        Task.Run(() =>
        {
            var region = _mapCameraEngine.CalculateBoundingRegion(snapshotPoints);

            if (region != null && !_lifecycleCts.IsCancellationRequested)
            {
                MainThread.BeginInvokeOnMainThread(() => LiveMap.MoveToRegion(region));
            }
        }, _lifecycleCts.Token).SafeFireAndForget(ex => AppLogger.Error("UI", ex, "FitMapToBounds crashed."));
    }
    private async void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RosterControl.UpdateConnectionStatus(status, color);
        });

        if (color == Colors.MediumSeaGreen)
        {
            var groupName = await MainThread.InvokeOnMainThreadAsync(() => GroupNameLabel.Text);
            var fetchedDetails = await _signalRService.GetGroupDetails(groupName);

            if (fetchedDetails != null)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var previousState = this.groupDetails?.CurrentState ?? GroupState.NotNavigating;

                    // 1. RIDER INERTIA & SERVER HEALING 
                    if (previousState >= GroupState.Navigating && fetchedDetails.CurrentState < GroupState.Navigating)
                    {
                        AppLogger.Info("Network", $"Server lost active ride state. Admin is enforcing {previousState} state.");

                        if (_amIAdmin)
                        {
                            if (previousState == GroupState.Navigating)
                            {
                                await _signalRService.StartGroupNavigation(groupName, groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                            }
                            else if (previousState == GroupState.Completed)
                            {
                                await _signalRService.CompleteGroupNavigation(groupName, _myName);
                            }
                            else
                            {
                                await _signalRService.StartGroupNavigation(groupName, groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                                await _signalRService.PauseGroupNavigation(groupName, "Restoring Pause State", _myName);
                            }
                        }
                        return;
                    }

                    this.groupDetails = fetchedDetails;

                    if (previousState != fetchedDetails.CurrentState)
                    {
                        GroupState serverState = fetchedDetails.CurrentState;

                        // Remove the redundant OnNavigationStarted trigger here during initial connection
                        if (previousState < GroupState.DestinationSet && serverState == GroupState.DestinationSet)
                        {
                            AppLogger.Info("Network", "Catching up: Destination set while offline.");
                            OnDestinationSet(fetchedDetails.DestLat, fetchedDetails.DestLng, fetchedDetails.DestName);
                        }
                        else if (previousState < GroupState.Navigating && serverState == GroupState.Navigating)
                        {
                            // Only run if the page has already completed its initial OnAppearing lifecycle
                            if (_hasJoined && _activeRouteLine == null)
                            {
                                AppLogger.Info("Network", "Catching up: Ride started while disconnected.");
                                OnNavigationStarted(fetchedDetails.DestLat, fetchedDetails.DestLng, fetchedDetails.DestName, isSyncRequired: true);
                            }
                        }
                        else
                        {
                            await ChangeGroupState(serverState, forceSync: true);
                        }
                    }
                });
            }
        }
    }
    private async Task RestorePausedStateSilentlyAsync(GroupState pausedState)
    {
        // 1. Ensure internal destination state is set properly
        _rideCache.ActiveDestination = new Location(groupDetails.DestLat, groupDetails.DestLng);
        _rideCache.ActiveDestinationName = groupDetails.DestName;
        DestinationSearchControl.SetDestinationText(groupDetails.DestName);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            TelemetryHeaderControl.SetDestinationName(groupDetails.DestName);
            UpdateDestinationPin(_rideCache.ActiveDestination, groupDetails.DestName);
            UpdateWaypointPins(_rideCache.ActiveWaypoints, _rideCache.ActiveDestination);
        });

        // 2. Draw the route if we have it in cache, otherwise fetch it quietly
        var currentLoc = _lastKnownLocation ?? await Geolocation.Default.GetLastKnownLocationAsync();

        bool canReuseCache = _rideCache.CachedMainRouteData != null &&
                             _rideCache.CachedMainRouteData.DecodedPoints != null &&
                             _rideCache.CachedMainRouteData.DecodedPoints.Any();

        if (canReuseCache)
        {
            // We have it! Just redraw it instantly without hitting Mapbox or running active nav logic.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_activeRouteLine != null) LiveMap.MapElements.Remove(_activeRouteLine);
                _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 22f };

                foreach (var coord in _rideCache.CachedMainRouteData.DecodedPoints)
                {
                    _activeRouteLine.Geopath.Add(coord);
                }

                LiveMap.MapElements.Add(_activeRouteLine);
                FitMapToBounds();
            });
        }
        else if (currentLoc != null && _rideCache.ActiveDestination != null)
        {
            // Cache was empty (e.g. app was wiped), fetch the route quietly in the background
            await CalculateAndDrawRoute(currentLoc, _rideCache.ActiveDestination, isMainRoute: true, isReroute: false, allowNetworkFetch: true);
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());
        }

        // 3. Immediately snap to the paused drawer UI
        await ChangeGroupState(pausedState, forceSync: true);
    }
    private async void OnMapClicked(object sender, MapClickedEventArgs e)
    {
        if (!_amIAdmin || groupDetails?.CurrentState == GroupState.Navigating) return;

        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Admin tapped map at {Lat}, {Lng}", flowId, e.Location.Latitude, e.Location.Longitude);

        try
        {
            LiveMap.MapElements.Clear();
            LiveMap.Pins.Clear();

            _pendingDestination = e.Location;
            string destName = $"{e.Location.Latitude:F4}, {e.Location.Longitude:F4}";

            try
            {
                var placemarks = await Geocoding.Default.GetPlacemarksAsync(e.Location.Latitude, e.Location.Longitude);
                var placemark = placemarks?.FirstOrDefault();
                if (placemark != null)
                {
                    destName = $"{placemark.FeatureName} {placemark.Thoroughfare}, {placemark.Locality}".Trim(' ', ',');
                }
            }
            catch (Exception geoEx)
            {
                _logger?.LogWarning(geoEx, "[{FlowId}] Reverse geocoding failed for map tap.", flowId);
            }

            DestinationSearchControl.InjectExternalSelection(destName, e.Location);
            UpdateDestinationPin(_pendingDestination, "Selected Destination");

            var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
            await CalculateAndDrawRoute(currentLoc, _pendingDestination);

            MainThread.BeginInvokeOnMainThread(async () => FitMapToBounds([currentLoc, _pendingDestination]));
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Map Click Processing", flowId);
        }
    }
    private void UpdateDestinationPin(Location location, string label)
    {
        // Remove only previous destination-like pins, keep rider pins and waypoint pins.
        var oldDestPins = LiveMap.Pins
            .Where(p =>
                p.Type == PinType.Place &&
                p.Label != "You" &&
                (string.IsNullOrEmpty(p.Label) || !p.Label.StartsWith("Waypoint ", StringComparison.Ordinal)))
            .ToList();

        foreach (var pin in oldDestPins)
            LiveMap.Pins.Remove(pin);

        LiveMap.Pins.Add(new Pin
        {
            Label = label,
            Location = location,
            Type = PinType.Place
        });
    }

    private void ClearWaypointPins()
    {
        var oldWaypointPins = LiveMap.Pins
            .Where(p =>
                p.Type == PinType.Place &&
                !string.IsNullOrEmpty(p.Label) &&
                p.Label.StartsWith("Waypoint ", StringComparison.Ordinal))
            .ToList();

        foreach (var pin in oldWaypointPins)
            LiveMap.Pins.Remove(pin);
    }

    private void UpdateWaypointPins(List<Location> waypoints, Location destination = null)
    {
        ClearWaypointPins();

        if (waypoints == null || waypoints.Count == 0) return;

        int idx = 1;
        foreach (var wp in waypoints)
        {
            // Skip endpoint if waypoint list includes final destination.
            if (destination != null &&
                Location.CalculateDistance(wp, destination, DistanceUnits.Kilometers) < 0.02)
            {
                continue;
            }

            LiveMap.Pins.Add(new Pin
            {
                Label = $"Waypoint {idx++}",
                Location = wp,
                Type = PinType.Place
            });
        }
    }
    // --- REPLACED MAP CAMERA MODES ---
    private void OnRecenterMapClicked(object sender, EventArgs e)
    {
        if (_lastKnownLocation != null)
        {
            // THE FIX: Return to default 0.5km zoom and gently rotate North
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(_lastKnownLocation, Distance.FromKilometers(0.5)));

            if (_myPinVm != null) _myPinVm.IsAutoCentering = true;

            OverviewButton.IsVisible = true;
            MapFollowButton.IsVisible = false;
        }
    }
    private async void OnOverviewClicked(object sender, EventArgs e)
    {
        if (_myPinVm == null) return;

        OverviewButton.IsVisible = false;
        MapFollowButton.IsVisible = true; // Show the "Overview" toggle

        _myPinVm.IsAutoCentering = false;

        await LiveMap.RotateToAsync(0, 500, Microsoft.Maui.Easing.SinInOut);
        LiveMap.Scale = 1.0;

        await Task.Delay(50);
        FitMapToBounds();
    }
    private async void OnEmergencyStopClicked(object sender, EventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Rider triggered Emergency Stop.", flowId);

        try
        {
            DrawerActionsTab.IsEnabled = false;
            await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Emergency", _myName);
        }
        catch (Exception ex) { await HandleExceptionAsync(ex, "Emergency Alert", flowId); }
        finally { DrawerActionsTab.IsEnabled = true; }
    }

    private async void OnRefuelStopClicked(object sender, EventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Rider triggered Refuel Stop.", flowId);

        try
        {
            DrawerActionsTab.IsEnabled = false;
            await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Refuel", _myName);
        }
        catch (Exception ex) { await HandleExceptionAsync(ex, "Refuel Alert", flowId); }
        finally { DrawerActionsTab.IsEnabled = true; }
    }

    private async void OnRestStopClicked(object sender, EventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Rider triggered Rest Stop.", flowId);

        try
        {
            DrawerActionsTab.IsEnabled = false;
            await _signalRService.SendGroupAlert(GroupNameLabel.Text, "Rest", _myName);
        }
        catch (Exception ex) { await HandleExceptionAsync(ex, "Rest Alert", flowId); }
        finally { DrawerActionsTab.IsEnabled = true; }
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
            else if (alertType.StartsWith("Formation_"))
            {
                string formation = alertType.Replace("Formation_", "").Replace("_", " ");
                voiceMessage = $"{senderName} requested a formation change. Switch to {formation} formation.";
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
            await ClosePageAsync();
        });
    }
    private async void OnLeaveGroupClicked(object sender, EventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew();

        bool confirm = await DisplayAlert("Leave Group", "Are you sure you want to permanently leave the group?", "Yes", "Cancel");
        if (confirm)
        {
            _logger?.LogInformation("[{FlowId}] User confirmed leaving group permanently.", flowId);
            try
            {
                _isLeavingGroupPermanently = true;
                _rideCache.HardResetAll();
                _signalRService.LeaveGroup(CurrentGoogleId).SafeFireAndForget();
                await ClosePageAsync();
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(ex, "Leave Group", flowId);
            }
        }
    }
    private void OnOpenSettingsClicked(object sender, EventArgs e) => AppInfo.Current.ShowSettingsUI();
    private void OnRetryLocationClicked(object sender, EventArgs e) => InitializeLocalTrackingAsync();
    private void OnRiderTapped(object sender, Rider selectedRider)
    {
        if (selectedRider == null) return;

        // Save this so we know who to apply the action to when the menu closes!
        _selectedRiderForManagement = selectedRider;

        string rawName = selectedRider.Name.Replace(" (You)", "").Replace(" (Offline)", "").Replace(" 👻 (Hidden)", "");
        bool isSelf = selectedRider.GoogleId == CurrentGoogleId;
        bool isHidden = _rideCache.HiddenRiders.Contains(rawName);

        // This instantly pops open your new UI!
        RiderManagementOverlay.Show(rawName, isHidden, _amIAdmin, isSelf);
    }
    private async void OnRiderManagementActionSelected(object sender, string action)
    {
        if (string.IsNullOrEmpty(action) || action == "Cancel" || _selectedRiderForManagement == null) return;

        string flowId = CorrelationContext.GenerateNew();
        Rider selectedRider = _selectedRiderForManagement;
        string rawName = selectedRider.Name.Replace(" (You)", "").Replace(" (Offline)", "").Replace(" 👻 (Hidden)", "");

        _logger?.LogInformation("[{FlowId}] Executing Rider Management action '{Action}' on user '{TargetUser}'.", flowId, action, rawName);

        try
        {
            bool isEssential = IsEssentialRole(selectedRider.Role, selectedRider.IsAdmin);

            if (action == "Show on Map" || action == "Hide from Map")
            {
                bool hide = action == "Hide from Map";

                if (hide && isEssential)
                {
                    bool confirm = await DisplayAlert("Hide Essential Rider?", $"{rawName} is the {selectedRider.Role}. It is highly recommended to keep them visible. Hide anyway?", "Hide", "Cancel");
                    if (!confirm) return;
                }

                if (hide) _rideCache.HiddenRiders.Add(rawName);
                else _rideCache.HiddenRiders.Remove(rawName);

                if (hide && _riderViewModels.TryGetValue(rawName, out var vm))
                {
                    MapPins.Remove(vm);
                    _riderViewModels.TryRemove(rawName, out _);
                }

                _signalRService.SendVisibilityToggle(GroupNameLabel.Text, rawName, hide).SafeFireAndForget();

                var roster = await _signalRService.GetGroupRoster(GroupNameLabel.Text);
                if (roster != null) OnRosterUpdated(roster);

                _logger?.LogInformation("[{FlowId}] Rider visibility toggled to Hidden={Hidden}.", flowId, hide);
                return;
            }

            if (!_amIAdmin) return;

            if (action == "View Emergency Info")
            {
                var emergencyData = await _signalRService.GetRiderEmergencyInfo(selectedRider.GoogleId);
                if (emergencyData != null)
                {
                    string info = $"Blood Group: {emergencyData.BloodGroup}\nContact: {emergencyData.EmergencyContact}\nVehicle: {emergencyData.VehicleNumber}";
                    await DisplayAlert($"{rawName}'s Info", info, "Close");
                }
            }
            else
            {
                string backendRole = action == "Standard Rider" ? RiderRole.Rider.ToString() : action;
                if (selectedRider.IsAdmin && backendRole != RiderRole.Lead.ToString())
                {
                    bool hasOtherLead = Riders.Any(r => r.GoogleId != selectedRider.GoogleId && RiderRoleParser.ParseOrDefault(r.Role) == RiderRole.Lead);
                    if (!hasOtherLead)
                    {
                        await DisplayAlert("Action Denied", "At least another rider should be the Lead before you reassign yourself.", "OK");
                        return;
                    }
                }

                await _signalRService.AssignRole(GroupNameLabel.Text, selectedRider.GoogleId, backendRole);
                _logger?.LogInformation("[{FlowId}] Role assigned successfully.", flowId);
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, $"Rider Management: {action}", flowId);
        }
    }
    private async void OnPauseNavClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        string flowId = CorrelationContext.GenerateNew();
        string reason = await DisplayActionSheet("Reason for Pause?", "Cancel", null,
            "Fuel Stop", "Food/Rest Break", "Scenic Viewpoint", "Mechanical Issue", "Wait for Stragglers");

        if (reason == "Cancel" || string.IsNullOrEmpty(reason)) return;

        GlobalLoadingOverlay.Show("Pausing Route...");
        _logger?.LogInformation("[{FlowId}] Admin pausing navigation. Reason: {Reason}", flowId, reason);

        try
        {
            await _signalRService.PauseGroupNavigation(GroupNameLabel.Text, reason, _myName);
            _logger?.LogInformation("[{FlowId}] Navigation paused successfully.", flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Pause Navigation", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }
    private async void OnResumeJourneyClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        string flowId = CorrelationContext.GenerateNew();
        GlobalLoadingOverlay.Show("Resuming...");
        _logger?.LogInformation("[{FlowId}] Admin resuming navigation.", flowId);

        try
        {
            await _signalRService.ResumeGroupNavigation(GroupNameLabel.Text, _myName);
            _logger?.LogInformation("[{FlowId}] Navigation resumed successfully.", flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Resume Navigation", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async void OnCompleteNavClicked(object sender, EventArgs e)
    {
        if (!_amIAdmin) return;

        string flowId = CorrelationContext.GenerateNew();
        bool confirm = await DisplayAlert("Complete Route", "Are you sure you want to end this journey? This will stop navigation for everyone.", "Finish Ride", "Cancel");
        if (!confirm) return;

        GlobalLoadingOverlay.Show("Completing Route...");
        _logger?.LogInformation("[{FlowId}] Admin completing navigation.", flowId);

        try
        {
            await _signalRService.CompleteGroupNavigation(GroupNameLabel.Text, _myName);
            _rideCts?.Cancel();
            _logger?.LogInformation("[{FlowId}] Navigation completed successfully.", flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Complete Navigation", flowId);
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
                _= _simulatorService?.StartSimulationAsync(() => groupDetails.CurrentState, _rideCts.Token, RideScenario.Baseline_Navigate_Clean);
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
                pitstop: s.PitstopDistanceMeters / 1000, // convert slider km back to meters!
                dynamicRouting: s.EnableDynamicRouting,
                minUpdate: s.MinUpdateDistanceMeters,
                sensitivity: s.DeviationSensitivityMeters,
                maxUpdate: s.MaxUpdateDistanceMeters
                //arrivalGeofence: 1000 // Hardcode for now
            );
        }
    }

    private async void OnSettingsSubmitted(object sender, ConvoySettingsSubmittedEventArgs e)
    {
        if (e.IsCreationMode) return; // Handled in MainPage

        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Admin updating group settings.", flowId);

        try
        {
            _logger?.LogInformation("[{FlowId}] Saving lag: {Lag}m, Splinter: {Splinter}m", flowId, e.MaxLagDistanceMeters, e.SplinterWarningDistanceMeters);

            await _signalRService.UpdateGroupSettings(GroupNameLabel.Text, new GroupSettingsDto
            {
                MaxLagDistanceMeters = e.MaxLagDistanceMeters,
                SplinterWarningDistanceMeters = e.SplinterWarningDistanceMeters,
                MaxGroupSize = e.MaxGroupSize,
                PitstopDistanceMeters = e.PitstopDistanceMeters * 1000,
                MinUpdateDistanceMeters = e.MinBroadcastDistanceMeters,
                MaxUpdateDistanceMeters = e.MaxBroadcastDistanceMeters,
                ArrivalGeofenceMeters = 1000,
                EnableDynamicRouting = e.EnableDynamicRouting,
                LeadRiderGoogleId = CurrentGoogleId
            });

            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100));
            _logger?.LogInformation("[{FlowId}] Group settings updated successfully.", flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Update Group Settings", flowId);
        }
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
    private async void OnLaunchNativeNavClicked(object sender, EventArgs e)
    {
        if (_rideCache.ActiveDestination == null) return;

        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] User launching native third-party navigation app.", flowId);

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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[{FlowId}] Failed to open native maps application.", flowId);
            await DisplayAlertAsync("Error", "Could not open map.", "OK");
        }
    }
    private void MapPinClicked(RiderPin pin)
    {
        // Handle pin click
    }
    private async void OnDestinationPreviewRequested(object sender, PlaceSelectedEventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew();
        _logger?.LogInformation("[{FlowId}] Admin requested preview for destination: {DestName}", flowId, e.Name);

        try
        {
            _pendingDestination = e.Location;
            _rideCache.ActiveWaypoints = e.RouteWaypoints ?? new List<Location>();

            UpdateDestinationPin(_pendingDestination, e.Name);
            UpdateWaypointPins(_rideCache.ActiveWaypoints, _pendingDestination);

            var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
            if (currentLoc != null)
            {
                var cameraBoundsPoints = new List<Location> { currentLoc };
                if (_rideCache.ActiveWaypoints.Any())
                    cameraBoundsPoints.AddRange(_rideCache.ActiveWaypoints);
                else
                    cameraBoundsPoints.Add(_pendingDestination);

                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds(cameraBoundsPoints));
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Destination Preview", flowId);
        }
    }

    private async void OnDestinationConfirmed(object sender, PlaceSelectedEventArgs e)
    {
        string flowId = CorrelationContext.GenerateNew(); // <-- Start new tracking flow
        _logger?.LogInformation("[{FlowId}] Admin confirmed destination: {DestName}", flowId, e.Name);
        try
        {
            string destName = e.Name;
            _rideCache.ActiveDestination = e.Location;
            _rideCache.ActiveWaypoints = e.RouteWaypoints ?? new List<Location>();
            groupDetails.DestLng = e.Location.Longitude;
            groupDetails.DestLat = e.Location.Latitude;
            groupDetails.DestName = destName;

            await ChangeGroupState(GroupState.DestinationSet, _myName);

            _logger?.LogInformation("[{FlowId}] Sending destination to SignalR.", flowId);

            await _signalRService.SetGroupDestination(GroupNameLabel.Text, e.Location.Latitude, e.Location.Longitude, destName);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Confirm Destination", flowId);
        }
    }

    private void OnDestinationCleared(object sender, EventArgs e)
    {
        LiveMap.MapElements.Remove(_activeRouteLine);
        LiveMap.MapElements.Clear();
        _activeRouteLine = null;
        _activeRouteSteps.Clear();

        DrawMapBubbles(new List<MapBubble>());
        ClearWaypointPins();

        var oldPins = LiveMap.Pins.Where(p => p.Type == PinType.Place && p.Label != "You").ToList();
        foreach (var p in oldPins) LiveMap.Pins.Remove(p);

        if (groupDetails?.CurrentState <= GroupState.DestinationSet)
        {
            _rideCache.HardResetAll();
            ChangeGroupState(GroupState.NotNavigating, _myName).SafeFireAndForget();
            _signalRService.CancelGroupNavigation(GroupNameLabel.Text).SafeFireAndForget();
        }
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
    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        // Simple and standard MAUI navigation: Pop this page off the stack!
        await ClosePageAsync();
    }
    private void OnAccelerometerReadingChanged(object sender, AccelerometerChangedEventArgs e)
    {
        // 1G = Standard gravity. > 4.5G = Severe, hard physical impact (crash)
        double gForce = Math.Sqrt(Math.Pow(e.Reading.Acceleration.X, 2) +
                                  Math.Pow(e.Reading.Acceleration.Y, 2) +
                                  Math.Pow(e.Reading.Acceleration.Z, 2));

        if (gForce > 4.5 && (DateTime.Now - _rideCache.LastCrashEvent).TotalMinutes > 5)
        {
            _rideCache.LastCrashEvent = DateTime.Now;
            TriggerCrashProtocol();
        }
    }
    private void DrawMapBubbles(List<MapBubble> bubbles)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (LiveMap.Handler is SpeedyCompass.Platforms.Android.CustomMapHandler handler)
            {
                handler.UpdateMapBubbles(bubbles);
            }
        });
#endif
    }
    private void OnVisibilityToggleReceived(string mutedByUserName, bool hide)
    {
        lock (_rideCache.UsersWhoMutedMe)
        {
            if (hide) _rideCache.UsersWhoMutedMe.Add(mutedByUserName);
            else _rideCache.UsersWhoMutedMe.Remove(mutedByUserName);
        }
    }
    private async Task ClosePageAsync()
    {
        if (Navigation.ModalStack.Count > 0)
        {
            await Navigation.PopModalAsync();
        }
        else
        {
            await Shell.Current.GoToAsync(".."); // Standard Shell back-navigation
        }
    }
    private void ApplyRouteUi(RouteUIData routeUi, bool isMainRoute, bool isNavigating, bool isReroute)
    {
        if (routeUi == null) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (isMainRoute)
            {
                _rideCache.CurrentRoutePoints = routeUi.DecodedPoints;
                _rideCache.CurrentRouteIndex = routeUi.SpliceIndex;
                _rideCache.OffRouteStrikeCount = 0;
                _rideCache.CurrentTrafficData = routeUi.TrafficData?.ToList() ?? new List<SpeedInterval>();

                if (PreNavDistLabel != null)
                    PreNavDistLabel.Text = $"{routeUi.DistanceKm} km, ETA {routeUi.EtaText}";

                if (isNavigating && !isReroute)
                {
                    TelemetryHeaderControl.UpdateTelemetryStats(
                        distText: $"{routeUi.DistanceKm} km",
                        distColor: Colors.DodgerBlue,
                        totalTravel: "0.0 km",
                        totalRoute: $"{routeUi.DistanceKm} km",
                        progressVal: 0.0,
                        progressPercent: "0%",
                        eta: ConvertDurationToArrivalTime(routeUi.EtaText),
                        isOffRoute: false
                    );
                }

                foreach (var line in _turnOverlayLines) LiveMap.MapElements.Remove(line);
                _turnOverlayLines.Clear();

                if (_activeRouteLine != null) LiveMap.MapElements.Remove(_activeRouteLine);
                _activeRouteLine = routeUi.MapLine;
                LiveMap.MapElements.Add(_activeRouteLine);

                if (routeUi.TurnOverlays != null)
                {
                    foreach (var whiteOverlay in routeUi.TurnOverlays)
                    {
                        _turnOverlayLines.Add(whiteOverlay);
                        LiveMap.MapElements.Add(whiteOverlay);
                    }
                }

                DrawMapBubbles(routeUi.MapBubbles);

                lock (_routeStateLock)
                {
                    _activeRouteSteps.Clear();
                    if (routeUi.VoiceSteps != null) _activeRouteSteps.AddRange(routeUi.VoiceSteps);
                    RebuildTurnOverlayStepMap(_activeRouteSteps);
                }
            }
            else
            {
                LiveMap.MapElements.Add(routeUi.MapLine);
                _otherRiderRoutes.Add(routeUi.MapLine);
            }
        });
    }
    private void RefreshBroadcastPrefsIfNeeded()
    {
        if ((DateTime.UtcNow - _lastBroadcastPrefsRefreshUtc).TotalSeconds < 10) return;

        _broadcastAggroMode = Preferences.Default.Get("Map_GPSUpdateAggressiveness", 0);
        _broadcastBatteryThrottle = Preferences.Default.Get("Map_BackgroundBatteryThrottlePercentage", 20);
        _broadcastLocalMinUpdate = Preferences.Default.Get("Map_LocalMinUpdate", 10.0);
        _broadcastLocalMaxUpdate = Preferences.Default.Get("Map_LocalMaxUpdate", 100.0);

        _lastBroadcastPrefsRefreshUtc = DateTime.UtcNow;
    }
    private async Task UpdateFreeTierTelemetry(Location currentLocation, double currentSpeedKmh, CancellationToken cancellationToken = default)
    {
        if (_rideCache.ActiveDestination == null) return;


        RouteTelemetryResult telemetry;
        try
        {
            telemetry = await _routingEngine.ProcessRouteTelemetryFreeAsync(
            currentLocation,
            _rideCache,
            currentSpeedKmh,
            cancellationToken);

            await _telemetryEngine.EvaluateEdgeTelemetryAsync(
                currentLocation,
                currentSpeedKmh,
                _myName,
                GroupNameLabel.Text,
                _amIAdmin,
                telemetry.DistLeftKm);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 4. Update the UI
        MainThread.BeginInvokeOnMainThread(() =>
        {
            TelemetryHeaderControl.UpdateTelemetryStats(
                    distText: telemetry.DistLeftStr,
                    distColor: Colors.DodgerBlue,
                    totalTravel: telemetry.TotalTravelStr,
                    totalRoute: telemetry.TotalRouteStr,
                    progressVal: telemetry.ProgressVal,
                    progressPercent: telemetry.ProgressPercentStr,
                    eta: telemetry.EtaStr,
                    isOffRoute: false
            );
        });
    }   
    private void ConfigureAlertPipelines()
    {
        _announceReroute = () =>
        {
            // Pro-only reroute voice
            if (_tierService.IsProTierEnabled && Preferences.Default.Get("Alerts_RerouteVoice", true))
                _voiceEngine.Speak("Rerouting...");
        };

        _announceDeviation = (userName) =>
        {
            if (Preferences.Default.Get("Alerts_DeviationVoice", true))
                _voiceEngine.Speak($"{userName} has diverted from the route.");
        };

        _announceTraffic = (message) =>
        {
            if (!string.IsNullOrWhiteSpace(message) &&
                _tierService.UseLiveTraffic &&
                Preferences.Default.Get("Alerts_TrafficVoice", true))
            {
                _voiceEngine.Speak(message);
            }
        };

        _announceWeather = (warningMessage) =>
        {
            if (!string.IsNullOrWhiteSpace(warningMessage) &&
                Preferences.Default.Get("Map_WeatherRadarEnabled", true) &&
                Preferences.Default.Get("Alerts_WeatherVoice", true))
            {
                _voiceEngine.Speak($"Weather alert. {warningMessage}");
            }
        };

        _announceElevation = (altMeters) =>
        {
            if (Preferences.Default.Get("Alerts_ElevationVoice", true))
                _voiceEngine.Speak($"Elevation is now {Math.Round(altMeters)} meters.");
        };
    }
    private static bool ShouldCreateTurnOverlay(RouteStep step)
    {
        var text = step?.Instruction?.Split(Environment.NewLine)[0]?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text.Contains("left") ||
               text.Contains("right") ||
               text.Contains("u-turn") ||
               text.Contains("roundabout") ||
               text.Contains("ramp") ||
               text.Contains("fork") ||
               text.Contains("merge") ||
               text.Contains("arrive") ||
               text.Contains("destination");
    }

    private void RebuildTurnOverlayStepMap(IEnumerable<RouteStep> steps)
    {
        _turnOverlayElementsPerStep.Clear();

        foreach (var step in steps)
        {
            // RoutingEngine creates: white polyline + arrow polygon => 2 elements
            _turnOverlayElementsPerStep.Enqueue(ShouldCreateTurnOverlay(step) ? 2 : 0);
        }
    }

    private void TrimConsumedTurnOverlays(int consumedSteps)
    {
        if (consumedSteps <= 0) return;

        int removeCount = 0;
        for (int i = 0; i < consumedSteps && _turnOverlayElementsPerStep.Count > 0; i++)
        {
            removeCount += _turnOverlayElementsPerStep.Dequeue();
        }

        if (removeCount <= 0) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            removeCount = Math.Min(removeCount, _turnOverlayLines.Count);
            for (int i = 0; i < removeCount; i++)
            {
                var element = _turnOverlayLines[0];
                LiveMap.MapElements.Remove(element);
                _turnOverlayLines.RemoveAt(0);
            }
        });
    }
    private async Task ActivateImmersiveGuidanceFromCacheAsync()
    {
        if (groupDetails?.CurrentState < GroupState.Navigating) return;
        if (!_tierService.UseImmersiveTbt) return;
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count < 2) return;

        List<RouteStep> activeSteps;
        List<Location> routePointsSnapshot;
        List<SpeedInterval> trafficSnapshot;

        lock (_routeStateLock)
        {
            activeSteps = _activeRouteSteps.Where(s => !s.StepCompleted).ToList();
            routePointsSnapshot = _rideCache.CurrentRoutePoints.ToList();
            trafficSnapshot = _rideCache.CurrentTrafficData?.ToList() ?? new List<SpeedInterval>();
        }

        if (activeSteps.Count == 0)
        {
            await MainThread.InvokeOnMainThreadAsync(() => NextTurnOverlay.IsVisible = false);
            return;
        }

        // Keep toggle snappy: build overlays only for near-term turns
        var overlaySteps = activeSteps.Take(10).ToList();

        var routeData = new RouteCalculationResult
        {
            EncodedPolyline = _rideCache.CachedMainRouteData?.EncodedPolyline
                              ?? _routingEngine.EncodeLocationList(routePointsSnapshot),
            DecodedPoints = routePointsSnapshot,
            VoiceSteps = overlaySteps,
            TrafficData = trafficSnapshot
        };

        RouteUIData? routeUi = null;

        await Task.Run(() =>
        {
            routeUi = _routingEngine.BuildRouteVisuals(
                routeData,
                Colors.DodgerBlue,
                generateOverlays: true,
                isReroute: false);
        });

        if (routeUi == null) return;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            foreach (var overlay in _turnOverlayLines)
                LiveMap.MapElements.Remove(overlay);

            _turnOverlayLines.Clear();
            _turnOverlayElementsPerStep.Clear();

            foreach (var overlay in routeUi.TurnOverlays)
            {
                _turnOverlayLines.Add(overlay);
                LiveMap.MapElements.Add(overlay);
            }

            DrawMapBubbles(routeUi.MapBubbles);
            RebuildTurnOverlayStepMap(overlaySteps);
        });
    }
    private static bool IsEssentialRole(string? role, bool isAdmin)
    {
        if (isAdmin) return true;

        return RiderRoleParser.ParseOrDefault(role) switch
        {
            RiderRole.Lead => true,
            RiderRole.Tail => true,
            RiderRole.Marshal => true,
            RiderRole.Admin => true,
            _ => false
        };
    }
    private async Task HandleExceptionAsync(Exception ex, string operationName, string flowId)
    {
        _logger?.LogError(ex, "[{FlowId}] Error during {OperationName}.", flowId, operationName);

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlertAsync("System Error", $"An unexpected error occurred.\n\nError Code: {flowId}", "OK");
        });
    }
    // ==========================================
    // ETA FORMATTER HELPER
    // ==========================================
    private string ConvertDurationToArrivalTime(string durationText)
    {
        if (string.IsNullOrWhiteSpace(durationText)) return "--:--";

        int hours = 0, minutes = 0;

        foreach (var part in durationText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith("h") && int.TryParse(part.TrimEnd('h'), out int h)) hours = h;
            if (part.EndsWith("m") && int.TryParse(part.TrimEnd('m'), out int m)) minutes = m;
        }

        return DateTime.Now.AddHours(hours).AddMinutes(minutes).ToString("h:mm tt");
    }
    private async void OnFormationChangeClicked(object sender, EventArgs e)
    {
        // Only Admins or designated Lead Riders should dictate the formation
        if (!_amIAdmin && _rideCache.MyRole != "Lead")
        {
            await DisplayAlertAsync("Permission Denied", "Only the Admin or Lead Rider can change the group formation.", "OK");
            return;
        }

        string flowId = CorrelationContext.GenerateNew();

        // 1. Pop the selection menu with the correct 6 formations
        string formation = await DisplayActionSheetAsync("Select Riding Formation", "Cancel", null,
            "Staggered",
            "Single File",
            "Double File",
            "Diamond",
            "V Formation",
            "Two Group");

        if (formation == "Cancel" || string.IsNullOrEmpty(formation)) return;

        // 2. Format it safely for the backend
        // E.g., "Two Group" -> "Formation_Two_Group"
        string alertTag = "Formation_" + formation.Replace(" ", "_");

        _logger?.LogInformation("[{FlowId}] Lead triggered formation change: {Formation}", flowId, formation);

        // 3. Lock UI and Broadcast
        try
        {
            SetActionButtonsEnabled(false);
            await _signalRService.SendGroupAlert(GroupNameLabel.Text, alertTag, _myName);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Formation Alert Broadcast", flowId);
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }
}