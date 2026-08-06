#if ANDROID
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
    private readonly string _googleApiKey;
    private static readonly HttpClient _httpClient = new();

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
    private readonly Random _randomColorGen = new();

    private bool _isSimulating = false;
    private CancellationTokenSource _debounceCts;

    private bool _isLeavingGroupPermanently = false;
    private bool _isSelectingLocation = false;

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
    private CancellationTokenSource _rideCts;
    private DateTime _lastNetworkBroadcastTime = DateTime.MinValue;
    private Location _lastNetworkBroadcastLocation = null;
    private static readonly (Color PinColor, Color RouteColor)[] RiderColors = new[]
    {
        (Color.FromArgb("#FFA07A"), Color.FromArgb("#8B0000")), // Light Salmon -> Dark Red
        (Color.FromArgb("#98FB98"), Color.FromArgb("#006400")), // Pale Green -> Dark Green
        (Color.FromArgb("#FFD700"), Color.FromArgb("#B8860B")), // Gold -> Dark Goldenrod
        (Color.FromArgb("#DDA0DD"), Color.FromArgb("#4B0082")), // Plum -> Indigo
        (Color.FromArgb("#FF69B4"), Color.FromArgb("#C71585")), // Hot Pink -> Medium Violet Red
        (Color.FromArgb("#87CEFA"), Color.FromArgb("#00008B")), // Light Sky Blue -> Dark Blue
        (Color.FromArgb("#F0E68C"), Color.FromArgb("#8B8000")), // Khaki -> Dark Yellow
        (Color.FromArgb("#E6E6FA"), Color.FromArgb("#483D8B")), // Lavender -> Dark Slate Blue
        (Color.FromArgb("#FFB6C1"), Color.FromArgb("#800000")), // Light Pink -> Maroon
        (Color.FromArgb("#20B2AA"), Color.FromArgb("#008080")), // Light Sea Green -> Teal
        (Color.FromArgb("#9370DB"), Color.FromArgb("#800080")), // Medium Purple -> Purple
        (Color.FromArgb("#00FA9A"), Color.FromArgb("#2E8B57")), // Medium Spring Green -> Sea Green
        (Color.FromArgb("#FFC0CB"), Color.FromArgb("#DC143C")), // Pink -> Crimson
        (Color.FromArgb("#B0E0E6"), Color.FromArgb("#4682B4")), // Powder Blue -> Steel Blue
        (Color.FromArgb("#F5DEB3"), Color.FromArgb("#A0522D")), // Wheat -> Sienna
        (Color.FromArgb("#D3D3D3"), Color.FromArgb("#696969")), // Light Gray -> Dim Gray
        (Color.FromArgb("#FFA500"), Color.FromArgb("#D2691E")), // Orange -> Chocolate
        (Color.FromArgb("#7FFFD4"), Color.FromArgb("#556B2F")), // Aquamarine -> Dark Olive Green
        (Color.FromArgb("#F08080"), Color.FromArgb("#B22222")), // Light Coral -> Firebrick
        (Color.FromArgb("#EEE8AA"), Color.FromArgb("#8B4513"))  // Pale Goldenrod -> Saddle Brown
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

        DeviceDisplay.Current.KeepScreenOn = Preferences.Default.Get("KeepScreenOn", false);

        _signalRService = signalRService;
        _voiceEngine = IPlatformApplication.Current?.Services.GetService<IVoiceCopilotEngine>();
        _rideCache = IPlatformApplication.Current?.Services.GetService<RideStateService>();
        _routingEngine = IPlatformApplication.Current?.Services.GetService<IRoutingEngine>();
        _telemetryEngine = IPlatformApplication.Current?.Services.GetService<ITelemetryEngine>();

        this.groupDetails = groupDetails;

#if ANDROID
        _locationTracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
        if (_locationTracker != null)
        {
            _locationTracker.LocationUpdated += OnLocalLocationPushedFromBackground;
        }
#endif

        _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk";

        GroupNameLabel.Text = this.groupDetails.GroupName;
        _myName = Preferences.Default.Get("username", "Unknown");
        _amIAdmin = this.groupDetails.AdminGoogleId == CurrentGoogleId;

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
                OnPropertyChanged(nameof(ConvoyPin));
                AdminPinCard.IsVisible = _amIAdmin;

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

        // 2. Automatically push it through the Gatekeeper to open the Drawer and update the UI!
        // (This will automatically call your existing OnDestinationSet visual drawing method)
        await ChangeGroupState(GroupState.DestinationSet);
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

        _isSimulating = false;
        _locationTracker?.StopTracking();

#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif

        if (!_isLeavingGroupPermanently)
        {
            _ = _signalRService.LeaveLobby();
        }
        await _signalRService.StopAsync();
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
            RidersCollectionView.ItemsSource = Riders;

            // 3. Lock down UI based on admin status
            AdminSearchUI.IsVisible = _amIAdmin;
            AdminInstructionBanner.IsVisible = _amIAdmin;
            //AdminSettingsBtn.IsVisible = _amIAdmin;
            AdminPinCard.IsVisible = _amIAdmin;
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LocationDisabledOverlay.IsVisible = false;
                if (_myPinVm != null)
                {
                    string newSpeedStr = $"{Math.Round(e.SpeedMph * 1.60934)} kmph";

                    // THE FIX: Push the speed directly to the Drawer UI!
                    if (MySpeedLabel != null) MySpeedLabel.Text = newSpeedStr;

                    // THE FIX: 60-FPS Fluid Animation for the Local Pin!
                    // This tells the UI to glide the pin smoothly to the new spot over 1000ms
                    AnimatePinMovement(_myPinVm, e.Location, e.Heading, 1000);
                    _myPinVm.Speed = newSpeedStr;

                    // THE FIX: Fluid Camera Tracking
                    if (_myPinVm.IsAutoCentering)
                    {
                        // Calculate zoom based on speed (closer when slow, further when fast)
                        double speedKmh = e.SpeedMph * 1.60934;
                        double radiusKm = speedKmh > 80 ? 1.5 : (speedKmh > 40 ? 1.0 : 0.5);

                        var newRegion = MapSpan.FromCenterAndRadius(e.Location, Distance.FromKilometers(radiusKm));

                        // We do NOT await this, let it fire and smoothly glide the camera
                        LiveMap.MoveToRegion(newRegion);
                    }
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

        if (groupDetails?.CurrentState == GroupState.Navigating)
        {
            // Fire Telemetry completely independently so it doesn't block the UI glide
            _ = TrimRouteVisuals(e.Location);

            double speedKmh = e.SpeedMph * 1.60934;
            _ = _telemetryEngine.EvaluateEdgeTelemetryAsync(e.Location, speedKmh, _myName, groupDetails.GroupName, _amIAdmin);

            _ = _telemetryEngine.EvaluateSpeedLimitAsync(e.Location, speedKmh, (limit, isSpeeding) =>
            {
                if (!_rideCache.RunningInBackground)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (limit == 0)
                        {
                            SpeedLimitBadge.IsVisible = false;
                            return;
                        }

                        SpeedLimitBadge.IsVisible = true;
                        SpeedLimitLabel.Text = limit.ToString();
                        MySpeedLabel.TextColor = isSpeeding ? Colors.Red : Colors.DodgerBlue;
                        SpeedLimitBadge.Stroke = isSpeeding ? Colors.Red : Colors.Gray;
                    });
                }
            });

            _voiceEngine?.ProcessTurnByTurn(e.Location, _activeRouteSteps);

            if (ShouldBroadcastToNetwork(e.Location, speedKmh))
            {
                _lastNetworkBroadcastTime = DateTime.UtcNow;
                _lastNetworkBroadcastLocation = e.Location;

                // Offload network call so it doesn't block the buttery-smooth UI glide!
                _ = Task.Run(async () => {
                    try
                    {
                        await _signalRService.UpdateLocation(groupDetails.GroupName, _myName, e.Location.Latitude, e.Location.Longitude, e.Heading);
                        AppLogger.Info("Network", $"Broadcasted location at {Math.Round(speedKmh)} km/h");
                    }
                    catch (Exception ex) { AppLogger.Error("Network", ex, "Failed to broadcast location."); }
                });
            }
        }
    }

    private async Task TrimRouteVisuals(Location currentLocation)
    {
        if (_activeRouteLine == null || _rideCache.ActiveDestination == null || _rideCache.CurrentRoutePoints.Count < 2) return;
        if (_rideCts == null || _rideCts.IsCancellationRequested) return;

        var currentRouteSnapshot = _rideCache.CurrentRoutePoints.ToList();

        try
        {
            var telemetryData = await Task.Run(() =>
            {
                // Throw an exception immediately if the token was cancelled
                _rideCts.Token.ThrowIfCancellationRequested();

                // --- Odometer Math ---

                double currentSpeedKmh = (currentLocation?.Speed ?? 0) * 3.6;

                if (_rideCache.LastOdometerLocation != null)
                {
                    double stepDistance = Location.CalculateDistance(_rideCache.LastOdometerLocation, currentLocation, DistanceUnits.Kilometers);
                    if (stepDistance > 0.01 && stepDistance < 20) _rideCache.CumulativeDistanceKm += stepDistance;
                }

                if (currentLocation?.Speed != null)
                {
                    double spdKmh = (currentLocation.Speed.Value) * 3.6;
                    if (spdKmh > _rideCache.MaxSpeedKmh) _rideCache.MaxSpeedKmh = spdKmh;

                    if (spdKmh < 2) { if (_rideCache.LastStopTime == null) _rideCache.LastStopTime = DateTime.Now; }
                    else if (_rideCache.LastStopTime != null)
                    {
                        _rideCache.TotalStoppedTime += (DateTime.Now - _rideCache.LastStopTime.Value);
                        _rideCache.LastStopTime = null;
                    }
                }

                int startIndex = Math.Max(0, _rideCache.CurrentRouteIndex - 5);
                int searchRange = Math.Min(currentRouteSnapshot.Count - startIndex, 50);

                double minDistance = double.MaxValue;
                int closestActualIndex = startIndex;

                for (int i = 0; i < searchRange; i++)
                {
                    int checkIndex = startIndex + i;
                    double dist = Location.CalculateDistance(currentLocation, currentRouteSnapshot[checkIndex], DistanceUnits.Kilometers);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        closestActualIndex = checkIndex;
                    }
                }

                double distLeft = 0;
                if (currentRouteSnapshot.Count > 1 && closestActualIndex < currentRouteSnapshot.Count)
                {
                    distLeft += Location.CalculateDistance(currentLocation, currentRouteSnapshot[closestActualIndex], DistanceUnits.Kilometers);
                    for (int j = closestActualIndex; j < currentRouteSnapshot.Count - 1; j++)
                    {
                        distLeft += Location.CalculateDistance(currentRouteSnapshot[j], currentRouteSnapshot[j + 1], DistanceUnits.Kilometers);
                    }
                }
                else distLeft = Location.CalculateDistance(currentLocation, _rideCache.ActiveDestination, DistanceUnits.Kilometers);

                double currentSpeed = (currentLocation?.Speed ?? 0) * 3.6;
                double movingAvg = Math.Max(currentSpeed, 40);
                double hoursLeft = distLeft / movingAvg;
                DateTime eta = DateTime.Now.AddHours(hoursLeft);

                return new
                {
                    IsOffRoute = minDistance > 0.1,
                    NewRouteIndex = closestActualIndex,
                    DistLeftStr = $"{Math.Round(distLeft, 1)} km",
                    TotalTravelStr = $"{Math.Round(_rideCache.CumulativeDistanceKm, 1)} km",
                    TotalRouteStr = $"{Math.Round(_rideCache.CumulativeDistanceKm + distLeft, 1)} km",
                    EtaStr = $"ETA {eta:HH:mm}"
                };
            }, _rideCts.Token);

            if (telemetryData.IsOffRoute)
            {
                if ((DateTime.Now - _rideCache.LastRerouteTime).TotalSeconds > 15)
                {
                    AppLogger.Info("Routing", "Rider is off route. Triggering recalculation.");
                    _rideCache.LastRerouteTime = DateTime.Now;

                    _ = Task.Run(async () => {
                        try
                        {
                            var activeIntermediates = new List<Location>();
                            if (_rideCache.ActiveMeetupPoint != null) activeIntermediates.Add(_rideCache.ActiveMeetupPoint);

                            string newPolyline = await CalculateAndDrawRoute(currentLocation, _rideCache.ActiveDestination);

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
                        catch (Exception ex)
                        {
                            AppLogger.Error("Routing", ex, "Failed to recalculate route during deviation.");
                        }
                    }, _rideCts.Token);
                }
                return;
            }

            _rideCache.CurrentRouteIndex = telemetryData.NewRouteIndex;
            _rideCache.LastOdometerLocation = currentLocation;

            MainThread.BeginInvokeOnMainThread(() => {
                if (_rideCts.IsCancellationRequested) return;
                try
                {
                    MyDistanceLabel.Text = telemetryData.DistLeftStr;
                    MyTotalTraveledLabel.Text = telemetryData.TotalTravelStr;
                    MyTotalRouteLabel.Text = telemetryData.TotalRouteStr;
                    MyEtaLabel.Text = telemetryData.EtaStr;
                    MyEtaLabel.IsVisible = true;
                }
                catch (Exception ex) { AppLogger.Error("UI", ex, "Failed to update UI stats."); }
            });
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("Telemetry", "Telemetry math cancelled gracefully.");
        }
        catch (Exception ex)
        {
            // THE SAVIOR: If math fails, it logs it and exits cleanly instead of freezing the app forever!
            AppLogger.Error("Telemetry", ex, "CRITICAL ERROR in TrimRouteVisuals loop.");
        }
    }
    // --- NEW: Close Button Handler ---
    private async void OnCloseRideSummaryClicked(object sender, EventArgs e)
    {
        RideSummaryOverlay.IsVisible = false;

        // THE FIX: Return the app to the idle Lobby state so the
        // Search Bar and other lobby controls fully unlock again!
        await ChangeGroupState(GroupState.NotNavigating);
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
    private async Task<string> CalculateAndDrawRoute(Location origin, Location dest, Location meetup = null, Color routeColor = null, string riderName = null, bool isMainRoute = true)
    {
        try
        {
            var requestBody = new RoutesRequest
            {
                Origin = new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = origin.Latitude, Longitude = origin.Longitude } } },
                Destination = new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = dest.Latitude, Longitude = dest.Longitude } } }
            };

            if (meetup != null)
            {
                requestBody.Intermediates = new List<RouteWaypoint> {
                    new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = meetup.Latitude, Longitude = meetup.Longitude } } }
                };
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
            if(groupDetails.CurrentState < GroupState.Navigating)
            {
                request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline");
            }
            else
            {
                request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline,routes.legs.steps.startLocation,routes.legs.steps.navigationInstruction");
            }
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var routeResult = JsonSerializer.Deserialize<RoutesResponse>(await response.Content.ReadAsStringAsync());
            var mainRoute = routeResult?.Routes?.FirstOrDefault();

            if (mainRoute != null)
            {
                double distKm = Math.Round(mainRoute.DistanceMeters / 1000.0, 1);

                // THE FIX: Parse the Google Routes ETA!
                string durationStr = mainRoute.Duration?.Replace("s", "") ?? "0";
                double.TryParse(durationStr, out double durationSecs);
                TimeSpan ts = TimeSpan.FromSeconds(durationSecs);
                string etaText = ts.Hours > 0 ? $"{ts.Hours}h {ts.Minutes}m" : $"{ts.Minutes}m";

                var decodedPoints = _rideCache.CurrentRoutePoints = _routingEngine.DecodeGooglePolyline(mainRoute.Polyline.EncodedPolyline);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (isMainRoute && mainRoute.Legs != null)
                    {
                        if (PreNavDistLabel != null)
                        {
                            PreNavDistLabel.Text = $"{distKm} km";
                        }
                        // Safely remove only the main route (leaving the spiderweb intact!)
                        if (_activeRouteLine != null) LiveMap.MapElements.Remove(_activeRouteLine);

                        _activeRouteLine = new Polyline
                        {
                            StrokeColor = routeColor ?? Colors.DodgerBlue,
                            StrokeWidth = 22f
                        };
                        foreach (var coord in _rideCache.CurrentRoutePoints) _activeRouteLine.Geopath.Add(coord);
                        LiveMap.MapElements.Add(_activeRouteLine);

                        _activeRouteSteps.Clear();
                        foreach (var leg in mainRoute.Legs)
                        {
                            if (leg.Steps == null) continue;
                            foreach (var step in leg.Steps)
                            {
                                if (step.NavigationInstruction != null && !string.IsNullOrEmpty(step.NavigationInstruction.Instructions) && step.StartLocation?.LatLng != null)
                                {
                                    _activeRouteSteps.Add(new RouteStep
                                    {
                                        TurnLocation = new Location(step.StartLocation.LatLng.Latitude, step.StartLocation.LatLng.Longitude),
                                        Instruction = step.NavigationInstruction.Instructions
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        // Draw a secondary spiderweb route
                        var otherLine = new Polyline
                        {
                            StrokeColor = routeColor ?? Colors.MediumPurple,
                            StrokeWidth = 15f
                        };
                        foreach (var coord in decodedPoints) otherLine.Geopath.Add(coord);

                        LiveMap.MapElements.Add(otherLine);
                        _otherRiderRoutes.Add(otherLine);

                        // Drop a label pin in the middle of their route line!
                        //if (!string.IsNullOrEmpty(riderName) && decodedPoints.Count > 0)
                        //{
                        //    int midIndex = decodedPoints.Count / 2;
                        //    var labelPin = new Pin
                        //    {
                        //        Location = decodedPoints[midIndex],
                        //        Label = $"{riderName}'s Route",
                        //        Type = PinType.Generic
                        //    };
                        //    LiveMap.Pins.Add(labelPin);
                        //    _otherRiderRoutePins.Add(labelPin);
                        //}
                    }
                });

                return mainRoute.Polyline.EncodedPolyline;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Routing Error: {ex.Message}"); }
        return null;
    }
    // --- NEW VOICE NAV VARIABLES ---
    private List<RouteStep> _activeRouteSteps = new();

    private void OnLeadRouteUpdated(string encodedPolyline)
    {
        _rideCache.CurrentRoutePoints = _routingEngine.DecodeGooglePolyline(encodedPolyline);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var oldLines = LiveMap.MapElements.OfType<Polyline>().ToList();
            foreach (var line in oldLines) LiveMap.MapElements.Remove(line);

            _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 8 };
            foreach (var coord in _rideCache.CurrentRoutePoints) _activeRouteLine.Geopath.Add(coord);
            LiveMap.MapElements.Add(_activeRouteLine);

            _voiceEngine.Speak("Map synced with Lead rider.");
        });
    }

    private void OnRouteDeviationAlert(string userName)
    {
        MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak($"{userName} has diverted from the route."));
    }
    private async Task GenerateMeetupPointAsync()
    {
        if (_rideCache.ActiveDestination == null || _lastKnownLocation == null) return;

        var riderLocations = _rideCache.OtherRiderLocations.Values.ToList();
        if (riderLocations.Count == 0) return; // Silent return if riding solo

        ShowLoading("Optimizing Convoy Routes...");
        try
        {
            var leadRoute = await _routingEngine.GetRouteDataAsync(_lastKnownLocation, _rideCache.ActiveDestination);
            if (leadRoute == null || leadRoute.DecodedPoints.Count == 0) return;

            var routeTasks = new List<Task<RouteCalculationResult>>();
            foreach (var loc in riderLocations) routeTasks.Add(_routingEngine.GetRouteDataAsync(loc, _rideCache.ActiveDestination));

            var otherRoutes = await Task.WhenAll(routeTasks);

            Location meetupPoint = leadRoute.DecodedPoints.Last();

            for (int i = leadRoute.DecodedPoints.Count - 1; i >= 0; i--)
            {
                Location pt = leadRoute.DecodedPoints[i];
                bool sharedByAll = true;

                foreach (var routeResult in otherRoutes)
                {
                    if (routeResult == null || routeResult.DecodedPoints == null || routeResult.DecodedPoints.Count == 0) continue;

                    bool foundNear = routeResult.DecodedPoints.Any(rPt => Location.CalculateDistance(pt, rPt, DistanceUnits.Kilometers) < 0.1);
                    if (!foundNear)
                    {
                        sharedByAll = false;
                        break;
                    }
                }

                if (sharedByAll) meetupPoint = pt;
                else break;
            }

            await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, meetupPoint.Latitude, meetupPoint.Longitude);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Meetup Error: {ex.Message}"); }
        finally { HideLoading(); }
    }
    private async void OnMeetupPointSet(double lat, double lng)
    {
        _rideCache.ActiveMeetupPoint = new Location(lat, lng);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LiveMap.Pins.Add(new Pin { Label = "Meetup Point", Location = _rideCache.ActiveMeetupPoint, Type = PinType.Generic });
        });

        if (_lastKnownLocation != null && _rideCache.ActiveDestination != null)
        {
            await CalculateAndDrawRoute(_lastKnownLocation, _rideCache.ActiveDestination, _rideCache.ActiveMeetupPoint);

            // THE FIX: Draw the spiderweb for ALL users, using the Dark Route Colors!
            MainThread.BeginInvokeOnMainThread(() => ClearOtherRiderRoutes());

            foreach (var rider in _rideCache.OtherRiderLocations)
            {
                var colorProfile = GetColorsForRider(rider.Key);

                _ = CalculateAndDrawRoute(
                    origin: rider.Value,
                    dest: _rideCache.ActiveDestination,
                    meetup: _rideCache.ActiveMeetupPoint,
                    routeColor: colorProfile.RouteColor, // Dark, highly visible line
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

        this.groupDetails.CurrentState = newState;
        _stateStartTime = DateTime.Now;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (newState)
            {
                case GroupState.DestinationSet:
                    OnDestinationSet(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                    _isSelectingLocation = true;
                    if (string.IsNullOrEmpty(DestinationSearchBar.Text))
                    {
                        DestinationSearchBar.Text = groupDetails.DestName;
                    }

                    // Toggle Headers
                    IdleHeader.IsVisible = false;
                    PreNavigationHeader.IsVisible = true;
                    TelemetryHeader.IsVisible = false;

                    AdminInstructionBanner.IsVisible = false;
                    ConfirmDestButton.IsVisible = false;
                    ResetDestButton.IsVisible = true;
                    _isSelectingLocation = false;

                    ActionDrawer.IsVisible = true;
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;

                   
                    break;

                case GroupState.NotNavigating:
                case GroupState.Completed:
                    // Toggle Headers
                    IdleHeader.IsVisible = true;
                    PreNavigationHeader.IsVisible = false;
                    TelemetryHeader.IsVisible = false;

                    // THE FIX: Show appropriate header message based on Role
                    AdminIdleHeader.IsVisible = _amIAdmin;
                    RiderIdleHeader.IsVisible = !_amIAdmin;

                    ActionDrawer.IsVisible = true;
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;

                    FloatingMapControls.IsVisible = false;
#if DEBUG
                    //SimSpeedFrame.IsVisible = false;
#endif
                    ConfirmDestButton.IsVisible = true;
                    ResetDestButton.IsVisible = false;
                    DestinationSearchBar.IsReadOnly = false;
                    DestinationSearchBar.Text = string.Empty;
                    AdminInstructionBanner.IsVisible = _amIAdmin;

                    // BULLETPROOF CLEANUP
                    LiveMap.MapElements.Clear();
                    LiveMap.Pins.Clear();
                    _activeRouteLine = null;
                    _activeRouteSteps.Clear();
                    ClearOtherRiderRoutes();
                    ClearTemporaryPois();

                    _locationTracker?.StopTracking();
                    _isSimulating = false;

                    FitMapToBounds();

#if ANDROID
                    MainActivity.IsInNavigationMode = false;
#endif
                    if (newState == GroupState.Completed)
                        _voiceEngine.Speak($"Navigation completed by {triggerUser}. Great ride!");

                    if (_amIAdmin)
                    {
                        PauseNavBtn.IsVisible = false;
                        ResumeNavBtn.IsVisible = false;
                        CompleteNavBtn.IsVisible = false;
                        MeetupPointBtn.IsVisible = false;
                    }

                    if (MySpeedLabel != null) MySpeedLabel.Text = "0 km/h";
                    break;

                case GroupState.Navigating:
                    // Toggle Headers
                    IdleHeader.IsVisible = false;
                    PreNavigationHeader.IsVisible = false;
                    TelemetryHeader.IsVisible = true;

                    // Auto-switch drawer to the "Safety" Actions tab
                    OnDrawerTabClicked(TabActionsBtn, EventArgs.Empty);

                    AdminInstructionBanner.IsVisible = false;
                    DestinationSearchBar.IsReadOnly = true;
                    ConfirmDestButton.IsVisible = false;
                    ResetDestButton.IsVisible = true;

                    FloatingMapControls.IsVisible = true;
#if DEBUG
                    //SimSpeedFrame.IsVisible = true;
#endif

                    TabAdminBtn.IsVisible = _amIAdmin;

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
        ShowLoading("Drawing Route...");
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
        finally { HideLoading(); }
    }

    private async void OnNavigationStarted(double destLat, double destLng, string destName, bool isSyncRequired = false)
    {

        AppLogger.ResetRideCorrelationId();
        _rideCts?.Cancel();
        _rideCts = new CancellationTokenSource();

        AppLogger.Info("Navigation", $"Starting route to {destName}...");

        _rideCache.ActiveDestination = new Location(destLat, destLng);
        _rideCache.ActiveDestinationName = destName;
        _isSelectingLocation = true;
        DestinationSearchBar.Text = destName;
        _isSelectingLocation = false;

        _rideCache.ResetTelemetryState();

        await ChangeGroupState(GroupState.Navigating, _myName);

        Location loc;
#if DEBUG
        loc = _lastKnownLocation ?? await Geolocation.Default.GetLastKnownLocationAsync();
#else
        loc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
#endif

        if (loc != null)
        {
            await CalculateAndDrawRoute(loc, _rideCache.ActiveDestination);
            // THE FIX: Start navigation zoomed in and pointing North instead of zooming out to FitMapToBounds!
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_myPinVm != null) _myPinVm.IsAutoCentering = true;

                LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(loc, Distance.FromKilometers(0.5)));
                LiveMap.RotateTo(0, 500, Easing.SinInOut);
            });

#if DEBUG
            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any() && !_isSimulating)
            {
                _ = SimulateMovementAlongRouteAsync();
            }
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
                // Populate the UI
                SummaryDestLabel.Text = finalSummary.DestinationName;
                SummaryDistLabel.Text = $"{finalSummary.TotalDistanceKm} km";
                SummaryTopSpeedLabel.Text = $"{finalSummary.TopSpeedKmh} km/h";
                SummaryAvgSpeedLabel.Text = $"{finalSummary.AverageMovingSpeedKmh} km/h";

                // Format times cleanly
                SummaryMovingTimeLabel.Text = finalSummary.MovingTime.ToString(@"hh\:mm\:ss");
                SummaryTotalTimeLabel.Text = finalSummary.TotalElapsedTime.ToString(@"hh\:mm\:ss");

                // Show the modal!
                RideSummaryOverlay.IsVisible = true;
            }
        });

        await ChangeGroupState(GroupState.Completed, adminName);
    }

    private async void OnNavigationCancelled()
    {
        // Wipe the spiderweb routes if navigation is cancelled
        MainThread.BeginInvokeOnMainThread(() => ClearOtherRiderRoutes());
#if ANDROID
        MainActivity.IsInNavigationMode = false;
#endif
        _locationTracker?.StopTracking();
        _rideCache.HardResetAll();
        await ChangeGroupState(GroupState.NotNavigating);
    }

    private async void OnResetDestinationClicked(object sender, EventArgs e)
    {
        if (groupDetails != null)
        {
            ConfirmDestButton.IsVisible = true;
            ResetDestButton.IsVisible = false;
            DestinationSearchBar.IsReadOnly = false;
            DestinationSearchBar.Text = string.Empty;

            // THE FIX: Nuke the blue line and the destination pins visually!
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
        await ChangeGroupState(GroupState.NotNavigating);
        await _signalRService.CancelGroupNavigation(GroupNameLabel.Text);
    }

    // --- REMAINING UTILITIES ---
    private async void OnStartJourneyClicked(object sender, EventArgs e)
    {
        ShowLoading("Starting Navigation...");
        try
        {
            StartJourneyButton.IsEnabled = false;
            await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _rideCache.ActiveDestination.Latitude, _rideCache.ActiveDestination.Longitude, DestinationSearchBar.Text);
        }
        finally { HideLoading(); }
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
        var targetPoints = points;
        if (targetPoints == null)
        {
            if (MapPins.Count == 0) return;
            // Snapshot the list so we don't get collection-modified errors in the background!
            targetPoints = MapPins.Select(p => p.Location).ToList();
        }

        if (targetPoints.Count == 1)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(targetPoints.First(), Distance.FromKilometers(1))));
            return;
        }

        // OFF-LOAD HEAVY MATH TO BACKGROUND THREAD
        Task.Run(() =>
        {
            double minLat = double.MaxValue, minLng = double.MaxValue;
            double maxLat = double.MinValue, maxLng = double.MinValue;

            foreach (var loc in targetPoints)
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

            var region = new MapSpan(new Location(centerLat, centerLng), latDistance, lngDistance);

            // BRING THE RESULT BACK TO THE UI THREAD
            MainThread.BeginInvokeOnMainThread(() => LiveMap.MoveToRegion(region));
        });
    }
    //private async void OnRefreshTelemetryClicked(object sender, EventArgs e)
    //{
    //    await RefreshTelemetryData();
    //}

    // Include your remaining hardware hooks, search UI logic, mapping interactions, PTT methods etc below exactly as they were...

    // (Omitted purely to save response space, but you keep your existing Search/PiP/Hardware/Alerts logic here unchanged)

    private async void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            StatusLabel.TextColor = color;
            StatusDot.BackgroundColor = color;
            //MapTabStatusDot.Fill = color;
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
                // Snapshot what our screen currently shows
                var previousState = this.groupDetails?.CurrentState ?? GroupState.NotNavigating;

                // Download the fresh data
                this.groupDetails = fetchedDetails;

                // THE FIX: If the server says the convoy changed state while we were offline, automatically catch up!
                if (previousState != fetchedDetails.CurrentState)
                {
                    _ = ChangeGroupState(fetchedDetails.CurrentState, forceSync: true);
                }
            }
        }
    }
    //private async void OnTabClicked(object sender, EventArgs e)
    //{
    //    bool calledFromNavStart = e is TabClickedEventArgs tce && tce.FromNavigationStarted;

    //    if (sender == TabRoster)
    //    {
    //        TabRoster.BackgroundColor = Colors.DodgerBlue;
    //        TabRoster.TextColor = Colors.White;
    //        TabMap.BackgroundColor = Colors.Transparent;
    //        TabMap.TextColor = Application.Current.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black;

    //        RosterView.IsVisible = true;
    //        MapView.IsVisible = false;
    //    }
    //    else if (sender == TabMap)
    //    {
    //        TabMap.BackgroundColor = Colors.DodgerBlue;
    //        TabMap.TextColor = Colors.White;
    //        TabRoster.BackgroundColor = Colors.Transparent;
    //        TabRoster.TextColor = Application.Current.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black;

    //        RosterView.IsVisible = false;
    //        MapView.IsVisible = true;

    //        if (!calledFromNavStart && _rideCache.ActiveDestination != null)
    //        {
    //            var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
    //            UpdateDestinationPin(_rideCache.ActiveDestination, DestinationSearchBar.Text ?? PendingDestinationLabel.Text ?? "Selected Destination");
    //            await CalculateAndDrawRoute(currentLoc, _rideCache.ActiveDestination);
    //            if (groupDetails.CurrentState < GroupState.Navigating)
    //            {
    //                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _rideCache.ActiveDestination]));
    //            }
    //        }
    //        else
    //        {
    //            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());
    //        }
    //    }
    //}
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

                // THE FIX: Actually calculate the route and show the distance!
                var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
                if (currentLoc != null)
                {
                    await CalculateAndDrawRoute(currentLoc, _pendingDestination);
                    MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _pendingDestination]));
                }

                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
            }
            else
            {
                await DisplayAlertAsync("Not Found", "Could not find that location.", "OK");
            }
        }
        catch (Exception) { await DisplayAlertAsync("Error", "Geocoding failed.", "OK"); }
    }
    private void UpdateDestinationPin(Location location, string label)
    {
        var oldDest = LiveMap.Pins.FirstOrDefault(p => p.Label != "You" && p.Type == PinType.Place);
        if (oldDest != null) LiveMap.Pins.Remove(oldDest);

        LiveMap.Pins.Add(new Pin() { Label = label, Location = location, Type = PinType.Place });
    }
    private async void OnConfirmDestinationClicked(object sender, EventArgs e)
    {
        if (_pendingDestination == null || _lastKnownLocation == null || string.IsNullOrEmpty(DestinationSearchBar.Text)) return;

        ConfirmDestButton.IsVisible = false;
        ResetDestButton.IsVisible = true;
        DestinationSearchBar.IsReadOnly = true;
        AdminInstructionBanner.IsVisible = false;

        string destName = DestinationSearchBar.Text ?? "Destination";
        _rideCache.ActiveDestination = _pendingDestination;
        groupDetails.DestLng = _pendingDestination.Longitude;
        groupDetails.DestLat = _pendingDestination.Latitude;
        groupDetails.DestName = destName;

        await ChangeGroupState(GroupState.DestinationSet);

        //OnTabClicked(TabRoster, EventArgs.Empty);

        await _signalRService.SetGroupDestination(GroupNameLabel.Text, _pendingDestination.Latitude, _pendingDestination.Longitude, destName);

        // THE FIX: Auto-generate the meetup point instantly!
        if (_amIAdmin)
        {
            _ = GenerateMeetupPointAsync();
        }
    }
    // --- REPLACED MAP CAMERA MODES ---
    private void OnRecenterMapClicked(object sender, EventArgs e)
    {
        if (_lastKnownLocation != null)
        {
            // THE FIX: Return to default 0.5km zoom and gently rotate North
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(_lastKnownLocation, Distance.FromKilometers(0.5)));
            LiveMap.RotateTo(0, 500, Easing.SinInOut);

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
                _voiceEngine.Speak(senderName);
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

                //await ShowPoisTemporarilyAsync(new List<string> { "gas_station" }, "⛽");
            }
            else if (alertType == "Rest")
            {
                SensoryAlertOverlay.BackgroundColor = Colors.DodgerBlue;
                AlertTitleLabel.Text = "REST STOP";
                AlertIconLabel.Text = "☕";
                durationSeconds = 5;
                voiceMessage = $"{senderName} requested a rest stop. Prepare to pull over soon.";

                //await ShowPoisTemporarilyAsync(new List<string> { "restaurant", "cafe" }, "🍽️");
            }

            AlertSenderLabel.Text = $"Triggered by: {senderName}";
            SensoryAlertOverlay.IsVisible = true;

            _voiceEngine.Speak(voiceMessage);

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
    //private async Task RefreshTelemetryData()
    //{
    //    var data = await _signalRService.GetGroupTelemetry(GroupNameLabel.Text);
    //    MainThread.BeginInvokeOnMainThread(() => TelemetryCollectionView.ItemsSource = data);
    //}
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
            _rideCts?.Cancel();
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
            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any() && !_isSimulating)
            {
                _ = SimulateMovementAlongRouteAsync();
            }
#endif
        }

        _voiceEngine.Speak($"Resuming Navigation to {groupDetails.DestName}. Ride safe!");
    }
    private void OnSizeSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        SizeSlider.Value = roundedValue;
        SizeValueLabel.Text = $"{roundedValue} Riders";
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
    private async void OnAdminSettingsClicked(object sender, EventArgs e)
    {
        var currentSettings = await _signalRService.GetGroupSettings(GroupNameLabel.Text);
        if (currentSettings != null)
        {
            // THE FIX: Clamp values to ensure they fall within the XAML Min/Max limits.
            // If the DB has 0 for a new feature, this prevents the Slider from crashing.
            LagSlider.Value = Math.Clamp(currentSettings.MaxLagDistanceMeters, LagSlider.Minimum, LagSlider.Maximum);
            SplinterSlider.Value = Math.Clamp(currentSettings.SplinterWarningDistanceMeters, SplinterSlider.Minimum, SplinterSlider.Maximum);
            SizeSlider.Value = Math.Clamp(currentSettings.MaxGroupSize, SizeSlider.Minimum, SizeSlider.Maximum);

            double pitstopKm = currentSettings.PitstopDistanceMeters / 1000.0;
            PitstopSlider.Value = Math.Clamp(pitstopKm, PitstopSlider.Minimum, PitstopSlider.Maximum);

            MinUpdateSlider.Value = Math.Clamp(currentSettings.MinUpdateDistanceMeters, MinUpdateSlider.Minimum, MinUpdateSlider.Maximum);
            MaxUpdateSlider.Value = Math.Clamp(currentSettings.MaxUpdateDistanceMeters, MaxUpdateSlider.Minimum, MaxUpdateSlider.Maximum);
        }
        AdminSettingsOverlay.IsVisible = true;
    }
    // --- NEW: Slider Value Handlers ---
    private void OnMinUpdateSliderChanged(object sender, ValueChangedEventArgs e)
    {
        MinUpdateValueLabel.Text = $"{Math.Round(e.NewValue)}m";
    }
    private void OnMaxUpdateSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 10.0) * 10;
        MaxUpdateSlider.Value = roundedValue;
        MaxUpdateValueLabel.Text = $"{roundedValue}m";
    }

    private void OnCloseSettingsClicked(object sender, EventArgs e) => AdminSettingsOverlay.IsVisible = false;
    private void OnLagSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 50.0) * 50;
        LagSlider.Value = roundedValue;
        LagValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue}m";
    }

    private void OnSplinterSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 100.0) * 100;
        SplinterSlider.Value = roundedValue;
        SplinterValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue}m";
    }
    private async void OnSaveSettingsClicked(object sender, EventArgs e)
    {
        int maxLag = (int)LagSlider.Value;
        int splinterDist = (int)SplinterSlider.Value;
        int maxSize = (int)SizeSlider.Value;
        int pitstopDistMeters = (int)PitstopSlider.Value * 1000;
        // --- NEW ---
        int minUpdate = (int)Math.Round(MinUpdateSlider.Value);
        int maxUpdate = (int)Math.Round(MaxUpdateSlider.Value);

        await _signalRService.UpdateGroupSettings(GroupNameLabel.Text, new GroupSettingsDto
        {
            MaxLagDistanceMeters = maxLag,
            SplinterWarningDistanceMeters = splinterDist,
            MaxGroupSize = maxSize,
            PitstopDistanceMeters = pitstopDistMeters,
            MinUpdateDistanceMeters = minUpdate,
            MaxUpdateDistanceMeters = maxUpdate,
            ArrivalGeofenceMeters = 1000,
            EnableDynamicRouting = DynamicRoutingSwitch.IsToggled,
            LeadRiderGoogleId = CurrentGoogleId
        });
        AdminSettingsOverlay.IsVisible = false;
        Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100));
    }
#if DEBUG
    private async void OnForceRerouteClicked(object sender, EventArgs e)
    {
        if (_lastKnownLocation == null || !_isSimulating) return;

        MainThread.BeginInvokeOnMainThread(() => _voiceEngine.Speak("Simulating route deviation."));

        // 1. Kill the current simulation loop so it stops fighting us
        _isSimulating = false;
        await Task.Delay(2500); // Wait a couple of seconds for the while-loop to gracefully exit

        // 2. Teleport the rider roughly 500 meters to the East 
        // (0.005 degrees longitude is about 500m near the equator/India)
        var offRouteLoc = new Location(_lastKnownLocation.Latitude, _lastKnownLocation.Longitude + 0.005);
        _lastKnownLocation = offRouteLoc;

        // Move the pin immediately so you can see the teleport
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_myPinVm != null) _myPinVm.Location = offRouteLoc;
            LiveMap.MoveToRegion(MapSpan.FromCenterAndRadius(offRouteLoc, Distance.FromKilometers(1)));
        });

        // 3. Force the Telemetry Engine to process this fake location.
        // Because it's > 100m from the line, this will trigger IsOffRoute = true and fire the Google Routes API!
        await TrimRouteVisuals(offRouteLoc);

        // 4. Wait for the new route to be calculated and drawn
        await Task.Delay(4000);

        // 5. Restart the simulation! It will now snapshot the NEW route points and drive along them.
        _ = SimulateMovementAlongRouteAsync();
    }
#endif
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
                _voiceEngine.Speak("You can now speak.");
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
            PttOverlay.IsVisible = false;
            PttCountdownLabel.IsVisible = false;
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
    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (groupDetails?.CurrentState == GroupState.Navigating) return;
        if (_isSelectingLocation) return;
        if (e.OldTextValue == e.NewTextValue) return;

        string query = e.NewTextValue;

        if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
        {
            MainThread.BeginInvokeOnMainThread(() => {
                SuggestionsFrame.IsVisible = false;
                AdminInstructionBanner.IsVisible = true;

                // THE FIX: If text is fully cleared (user hit the 'X'), wipe the map routes!
                if (string.IsNullOrWhiteSpace(query))
                {
                    if (_activeRouteLine != null)
                    {
                        LiveMap.MapElements.Remove(_activeRouteLine);
                        _activeRouteLine = null;
                        _activeRouteSteps.Clear();
                    }
                    var oldPins = LiveMap.Pins.Where(p => p.Type == PinType.Place && p.Label != "You").ToList();
                    foreach (var p in oldPins) LiveMap.Pins.Remove(p);

                    // Revert UI out of "DestinationSet" state safely
                    if (groupDetails?.CurrentState == GroupState.DestinationSet)
                    {
                        _rideCache.HardResetAll();
                        _ = ChangeGroupState(GroupState.NotNavigating);
                    }
                }
            });
            return;
        }

        AdminInstructionBanner.IsVisible = false;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            // PROPER DEBOUNCE: Wait 500ms before hitting the API
            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;

            var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

            var reqBody = new AutocompleteRequest { Input = query };
            request.Content = new StringContent(JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<AutocompleteResponse>(responseBody);

            if (token.IsCancellationRequested) return;

            if (result != null && result.Suggestions != null && result.Suggestions.Any())
            {
                var displayList = result.Suggestions
                    .Where(s => s.PlacePrediction != null)
                    .Select(s => new UIPlaceSuggestion
                    {
                        Description = s.PlacePrediction.Text.Text,
                        PlaceId = s.PlacePrediction.PlaceId
                    }).ToList();

                // THE FIX: Force the UI updates back onto the Main Thread!
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SuggestionsListView.ItemsSource = displayList;
                    SuggestionsFrame.IsVisible = true;
                });
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => SuggestionsFrame.IsVisible = false);
            }
        }
        catch (TaskCanceledException) { /* Ignored, user is still typing */ }
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
    // --- NEW: Quick local tracker for calculating other riders' speeds ---
    private readonly Dictionary<string, DateTime> _riderLastUpdateTimes = new();
    private void OnRiderLocationUpdated(string riderId, double lat, double lng, double heading)
    {
        var newLoc = new Location(lat, lng);
        var now = DateTime.UtcNow;

        Task.Run(() =>
        {
            double speedKmh = 0;

            if (_rideCache.OtherRiderLocations.TryGetValue(riderId, out var oldLoc) &&
                _riderLastUpdateTimes.TryGetValue(riderId, out var lastTime))
            {
                double distKm = Location.CalculateDistance(oldLoc, newLoc, DistanceUnits.Kilometers);
                double hours = (now - lastTime).TotalHours;

                if (hours > 0)
                {
                    speedKmh = distKm / hours;
                    if (speedKmh > 250) speedKmh = _rideCache.OtherRiderSpeeds.GetValueOrDefault(riderId, 0);
                }
            }

            _rideCache.OtherRiderLocations[riderId] = newLoc;
            _rideCache.OtherRiderSpeeds[riderId] = speedKmh;
            _riderLastUpdateTimes[riderId] = now;

            string speedStr = speedKmh > 1 ? $"{Math.Round(speedKmh)} km/h" : "Stopped";

            // =======================================================
            // THE FIX: LOCAL AHEAD/BEHIND MATH
            // =======================================================
            string gapStatus = "Nearby";
            Color gapColor = Colors.MediumSeaGreen;

            if (_lastKnownLocation != null && _rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Count > 0)
            {
                double distToThemKm = Location.CalculateDistance(_lastKnownLocation, newLoc, DistanceUnits.Kilometers);
                double distToThemMeters = distToThemKm * 1000;

                if (distToThemMeters > 50) // If they are further than 50 meters away
                {
                    int theirIndex = 0;
                    double minDist = double.MaxValue;

                    // Find where they are on the route line
                    // Optimization: We check every 5th point to save CPU power!
                    for (int i = 0; i < _rideCache.CurrentRoutePoints.Count; i += 5)
                    {
                        double d = Location.CalculateDistance(newLoc, _rideCache.CurrentRoutePoints[i], DistanceUnits.Kilometers);
                        if (d < minDist)
                        {
                            minDist = d;
                            theirIndex = i;
                        }
                    }

                    string distDisplay = distToThemMeters > 1000 ? $"{Math.Round(distToThemKm, 1)} km" : $"{Math.Round(distToThemMeters)}m";

                    // Compare their route index to our route index!
                    if (theirIndex > _rideCache.CurrentRouteIndex + 5)
                    {
                        gapStatus = $"{distDisplay} Ahead";
                        gapColor = Colors.DodgerBlue;
                    }
                    else if (theirIndex < _rideCache.CurrentRouteIndex - 5)
                    {
                        gapStatus = $"{distDisplay} Behind";

                        // Check if they are falling too far behind based on Admin settings!
                        int lagLimit = groupDetails?.Settings?.MaxLagDistanceMeters ?? 1000;
                        gapColor = distToThemMeters > lagLimit ? Colors.Red : Colors.Orange;
                    }
                    else
                    {
                        gapStatus = $"{distDisplay} Away";
                        gapColor = Colors.Gray;
                    }
                }
            }
            // =======================================================

            if (_rideCache.RunningInBackground) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var riderModel = Riders.FirstOrDefault(r => r.Name != null && r.Name.StartsWith(riderId));
                if (riderModel != null)
                {
                    riderModel.SpeedStr = speedStr;
                    // Inject the live math into the UI
                    riderModel.StatusStr = gapStatus;
                    riderModel.StatusColor = gapColor;
                }

                if (_riderViewModels.TryGetValue(riderId, out var existingVm))
                {
                    existingVm.Location = newLoc;
                    existingVm.Heading = heading;
                    existingVm.Speed = speedStr;
                }
                else
                {
                    var colorProfile = GetColorsForRider(riderId);
                    var newVm = new RiderPin(MapPinClicked)
                    {
                        Username = riderId,
                        Speed = speedStr,
                        Location = newLoc,
                        Heading = heading,
                        PinColor = colorProfile.PinColor,
                        ZIndex = 50F
                    };

                    _riderViewModels.TryAdd(riderId, newVm);
                    MapPins.Add(newVm);
                }
            });
        });
    }
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
                    DrawerStatsTab.IsVisible = true;

                    var usedColors = MapPins.Where(pin => pin.Username != "You").Select(pin => pin.PinColor).ToHashSet();
                    Color uniqueColor;
                    var rand = new Random();
                    do { uniqueColor = Color.FromRgb(rand.Next(50, 230), rand.Next(50, 230), rand.Next(50, 230)); } while (usedColors.Contains(uniqueColor));

                    if (_myPinVm == null)
                    {
                        _myPinVm = new RiderPin(MapPinClicked)
                        {
                            Username = "You",
                            Speed = "0 km/h",
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
    private async Task SimulateMovementAlongRouteAsync()
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count == 0) return;

        // FIX: Prevent multiple overlapping simulations if they stop/start the route
        if (_isSimulating) return;

        await Task.Delay(2000);
        _isSimulating = true;

        if (_locationTracker != null) _locationTracker.IsSimulating = true;

        // Freeze a copy of the route! 
        var simulationPath = _rideCache.CurrentRoutePoints.ToList();
        int currentIndex = 0;

        AppLogger.Info("Simulator", "Starting route simulation...");

        while (currentIndex < simulationPath.Count)
        {
            // THE FIX 4: Check the cancellation token (_rideCts) to kill zombie threads instantly!
            if (groupDetails.CurrentState != GroupState.Navigating || !_isSimulating || (_rideCts != null && _rideCts.IsCancellationRequested))
            {
                _isSimulating = false;
                break;
            }

            double speedKmh = 60;

            if (speedKmh == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            var point = simulationPath[currentIndex];

            double fakeHeading = _lastKnownLocation != null
                ? CalculateBearing(_lastKnownLocation, point)
                : 0;

            int delayMs = 2000;
            if (_lastKnownLocation != null)
            {
                double distKm = Location.CalculateDistance(_lastKnownLocation, point, DistanceUnits.Kilometers);
                if (distKm > 0)
                {
                    double timeHours = distKm / speedKmh;
                    delayMs = (int)(timeHours * 3600000);
                    delayMs = Math.Clamp(delayMs, 100, 5000);
                }
            }

            if (!_rideCache.RunningInBackground)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (_myPinVm != null)
                    {
                        // THE FIX 3: Unleash the smooth animation!
                        AnimatePinMovement(_myPinVm, point, fakeHeading, (uint)delayMs);
                        string newSpeedStr = $"{Math.Round(speedKmh)} km/h";
                        _myPinVm.Speed = newSpeedStr;

                        // THE FIX: Push the simulated speed directly to the Drawer UI!
                        if (MySpeedLabel != null) MySpeedLabel.Text = newSpeedStr;

                        // THE FIX 2: Make the camera follow the simulator too!
                        //if (_myPinVm.IsAutoCentering)
                        //{
                        //    double radiusKm = speedKmh > 80 ? 1.5 : (speedKmh > 40 ? 1.0 : 0.5);
                        //    var newRegion = MapSpan.FromCenterAndRadius(point, Distance.FromKilometers(radiusKm));
                        //    LiveMap.MoveToRegion(newRegion); // Smoothly glide the map!
                        //}
                    }
                });
            }

            _lastKnownLocation = point;

            // --- THE FIX 1: Use the NEW Gatekeeper we just built! ---
            if (ShouldBroadcastToNetwork(point, speedKmh))
            {
                _lastNetworkBroadcastTime = DateTime.UtcNow;
                _lastNetworkBroadcastLocation = point;

                await _signalRService.UpdateLocation(GroupNameLabel.Text, _myName, point.Latitude, point.Longitude, fakeHeading);
                AppLogger.Info("Simulator", $"Broadcasted at {Math.Round(speedKmh)} km/h");
            }

            await TrimRouteVisuals(point);

            await _telemetryEngine.EvaluateEdgeTelemetryAsync(point, speedKmh, _myName, groupDetails.GroupName, _amIAdmin);

            // Fire the Speed Limit Engine
            _ = _telemetryEngine.EvaluateSpeedLimitAsync(point, speedKmh, (limit, isSpeeding) =>
            {
                if (_rideCache.RunningInBackground) return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (limit == 0)
                    {
                        SpeedLimitBadge.IsVisible = false;
                        return;
                    }

                    SpeedLimitBadge.IsVisible = true;
                    SpeedLimitLabel.Text = limit.ToString();
                    MySpeedLabel.TextColor = isSpeeding ? Colors.Red : Colors.DodgerBlue;
                    SpeedLimitBadge.Stroke = isSpeeding ? Colors.Red : Colors.Gray;
                });
            });

            _voiceEngine?.ProcessTurnByTurn(point, _activeRouteSteps);

            await Task.Delay(delayMs);
            currentIndex++;
        }

        _isSimulating = false;
        AppLogger.Info("Simulator", "Simulation ended cleanly.");
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
    private double _simSpeedKmh = 30; // Default slider value
    // --- NEW: Slider Handler ---
    private void OnSimSpeedSliderChanged(object sender, ValueChangedEventArgs e)
    {
        _simSpeedKmh = e.NewValue;
    }
    private List<Pin> _temporaryPoiPins = new();
    // =====================================================================
    // --- UPDATED: SEARCH-ALONG-ROUTE POI INJECTION ---
    // =====================================================================
    private async Task ShowPoisTemporarilyAsync(List<string> placeTypes, string emoji)
    {
        var currentLoc = _rideCache?.LastOdometerLocation ?? _lastKnownLocation;
        if (currentLoc == null) return;

        try
        {
            var activePoints = _rideCache?.CurrentRoutePoints;
            bool hasActiveRoute = activePoints != null && activePoints.Count > 2;

            HttpRequestMessage request;

            // 1. DYNAMIC API ROUTING
            if (hasActiveRoute)
            {
                // Convert list (e.g., ["gas_station"]) to natural text ("gas station") for the new API
                string textQuery = string.Join(" OR ", placeTypes.Select(t => t.Replace("_", " ")));

                // Grab up to the next 500 GPS nodes (roughly 50-80 km of upcoming curves)
                var upcomingPath = activePoints.Take(500).ToList();
                string encodedPath = _routingEngine.EncodeLocationList(upcomingPath);

                var requestBody = new SearchTextRequest
                {
                    TextQuery = textQuery,
                    SearchAlongRouteParameters = new SearchAlongRouteParameters
                    {
                        Polyline = new RoutePolyline { EncodedPolyline = encodedPath }
                    }
                };

                request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText");
                request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");
            }
            else
            {
                // Fallback: If no route is active, search in a 10km circle
                var requestBody = new NearbySearchRequest
                {
                    IncludedTypes = placeTypes,
                    MaxResultCount = 10,
                    LocationRestriction = new LocationRestriction
                    {
                        Circle = new SearchCircle
                        {
                            Center = new RouteLatLng { Latitude = currentLoc.Latitude, Longitude = currentLoc.Longitude },
                            Radius = 10000.0
                        }
                    }
                };

                request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchNearby");
                request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");
            }

            // 2. EXECUTE THE CALL
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
            request.Headers.Add("X-Goog-FieldMask", "places.displayName,places.location,places.rating");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();

                // Both APIs thankfully return the identical '{ places: [...] }' schema!
                var result = JsonSerializer.Deserialize<NearbySearchResponse>(responseBody);

                if (result?.Places != null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // 3. Clear existing & drop new pins
                        foreach (var oldPin in _temporaryPoiPins) LiveMap.Pins.Remove(oldPin);
                        _temporaryPoiPins.Clear();

                        foreach (var place in result.Places)
                        {
                            string ratingText = place.Rating > 0 ? $"{place.Rating} ⭐" : "No reviews";
                            var pin = new Pin
                            {
                                Label = $"{emoji} {place.DisplayName?.Text}",
                                Address = ratingText,
                                Type = PinType.Place,
                                Location = new Location(place.Location.Latitude, place.Location.Longitude)
                            };

                            _temporaryPoiPins.Add(pin);
                            LiveMap.Pins.Add(pin);
                        }

                        ClearPoisBtn.IsVisible = true;
                    });

                    // 4. Auto-clean after 10 mins
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10));
                        ClearTemporaryPois();
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"POI Fetch Error: {ex.Message}");
        }
    }

    // --- NEW: Manual POI Cleanup Logic ---
    private void OnClearPoisClicked(object sender, EventArgs e)
    {
        ClearTemporaryPois();
    }

    private void ClearTemporaryPois()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var oldPin in _temporaryPoiPins)
            {
                LiveMap.Pins.Remove(oldPin);
            }
            _temporaryPoiPins.Clear();

            // Hide the button once the map is clean
            ClearPoisBtn.IsVisible = false;
        });
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
        TrafficSwitch.IsToggled = Preferences.Default.Get("Map_Traffic", true);
        SpeedLimitSwitch.IsToggled = Preferences.Default.Get("Map_SpeedLimits", true);
        AutoZoomSwitch.IsToggled = Preferences.Default.Get("Map_AutoZoom", true);
        AutoTiltSwitch.IsToggled = Preferences.Default.Get("Map_AutoTilt", true);

        LiveMap.IsTrafficEnabled = TrafficSwitch.IsToggled;

        int mapType = Preferences.Default.Get("Map_Style", (int)Microsoft.Maui.Maps.MapType.Street);
        LiveMap.MapType = (Microsoft.Maui.Maps.MapType)mapType;
        UpdateMapStyleButtons(mapType);

        // THE FIX: Load the Compass button state!
        _isHeadingUp = Preferences.Default.Get("Map_HeadingUp", false);
        HeadingUpButton.BackgroundColor = _isHeadingUp ? Colors.DodgerBlue : (Application.Current.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#333333") : Colors.White);
        HeadingUpButton.TextColor = _isHeadingUp ? Colors.White : Colors.DodgerBlue;

        if (VoiceNavSwitch != null)
            VoiceNavSwitch.IsToggled = Preferences.Default.Get("Map_VoiceNav", true);
    }
    private void OnMapSettingChanged(object sender, ToggledEventArgs e)
    {
        Preferences.Default.Set("Map_Traffic", TrafficSwitch.IsToggled);
        Preferences.Default.Set("Map_SpeedLimits", SpeedLimitSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoZoom", AutoZoomSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoTilt", AutoTiltSwitch.IsToggled);

        if (VoiceNavSwitch != null)
            Preferences.Default.Set("Map_VoiceNav", VoiceNavSwitch.IsToggled);

        LiveMap.IsTrafficEnabled = TrafficSwitch.IsToggled;
    }
    private void OnMapStyleClicked(object sender, EventArgs e)
    {
        int mapType = (int)MapType.Street;
        if (sender == MapStyleSatBtn) mapType = (int)MapType.Satellite;
        if (sender == MapStyleTerBtn) mapType = (int)MapType.Hybrid; // Excellent for Moto trips!

        LiveMap.MapType = (MapType)mapType;
        Preferences.Default.Set("Map_Style", mapType);
        UpdateMapStyleButtons(mapType);
    }
    private void UpdateMapStyleButtons(int activeType)
    {
        MapStyleStreetBtn.BackgroundColor = activeType == (int)MapType.Street ? Colors.DodgerBlue : Colors.Transparent;
        MapStyleStreetBtn.TextColor = activeType == (int)MapType.Street ? Colors.White : Colors.Gray;
        MapStyleSatBtn.BackgroundColor = activeType == (int)MapType.Satellite ? Colors.DodgerBlue : Colors.Transparent;
        MapStyleSatBtn.TextColor = activeType == (int)MapType.Satellite ? Colors.White : Colors.Gray;
        MapStyleTerBtn.BackgroundColor = activeType == (int)MapType.Hybrid ? Colors.DodgerBlue : Colors.Transparent;
        MapStyleTerBtn.TextColor = activeType == (int)MapType.Hybrid ? Colors.White : Colors.Gray;
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
            if (!_amIAdmin)
            {
                PauseNavBtn.IsVisible = false;
                ResumeNavBtn.IsVisible = false;
                CompleteNavBtn.IsVisible = false;
                MeetupPointBtn.IsVisible = false;
                return;
            }

            // The Sync/Meetup button ONLY appears if there are other online riders!
            bool hasMultipleRiders = Riders.Count(r => r.IsOnline) > 1;

            if (groupDetails?.CurrentState == GroupState.Navigating)
            {
                PauseNavBtn.IsVisible = true;
                ResumeNavBtn.IsVisible = false;
                CompleteNavBtn.IsVisible = true;
                MeetupPointBtn.IsVisible = hasMultipleRiders;
            }
            else if (groupDetails?.CurrentState == GroupState.PausedBreak ||
                     groupDetails?.CurrentState == GroupState.PausedHazard ||
                     groupDetails?.CurrentState == GroupState.PausedMechanical)
            {
                PauseNavBtn.IsVisible = false;
                ResumeNavBtn.IsVisible = true;
                CompleteNavBtn.IsVisible = true;
                MeetupPointBtn.IsVisible = hasMultipleRiders;
            }
            else if (groupDetails?.CurrentState == GroupState.DestinationSet)
            {
                PauseNavBtn.IsVisible = false;
                ResumeNavBtn.IsVisible = false;
                CompleteNavBtn.IsVisible = false;
                MeetupPointBtn.IsVisible = hasMultipleRiders;
            }
            else
            {
                // NotNavigating or Completed
                PauseNavBtn.IsVisible = false;
                ResumeNavBtn.IsVisible = false;
                CompleteNavBtn.IsVisible = false;
                MeetupPointBtn.IsVisible = false;
            }
        });
    }
}