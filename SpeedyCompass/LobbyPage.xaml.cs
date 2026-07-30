#if ANDROID
using AndroidX.ConstraintLayout.Core.Motion.Utils;
using Kotlin.Contracts;

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
public class RoutesRequest
{
    [JsonPropertyName("origin")] public RouteWaypoint Origin { get; set; }
    [JsonPropertyName("destination")] public RouteWaypoint Destination { get; set; }
    [JsonPropertyName("intermediates")] public List<RouteWaypoint> Intermediates { get; set; }
    [JsonPropertyName("travelMode")] public string TravelMode { get; set; } = "DRIVE";
}
public class RouteWaypoint { [JsonPropertyName("location")] public RouteLocation Location { get; set; } }
public class RouteLocation { [JsonPropertyName("latLng")] public RouteLatLng LatLng { get; set; } }
public class RouteLatLng { [JsonPropertyName("latitude")] public double Latitude { get; set; } [JsonPropertyName("longitude")] public double Longitude { get; set; } }
public class RoutesResponse { [JsonPropertyName("routes")] public List<RouteData> Routes { get; set; } }
public class RouteData { [JsonPropertyName("distanceMeters")] public int DistanceMeters { get; set; } [JsonPropertyName("duration")] public string Duration { get; set; } [JsonPropertyName("polyline")] public RoutePolyline Polyline { get; set; } }
public class RoutePolyline { [JsonPropertyName("encodedPolyline")] public string EncodedPolyline { get; set; } }
public class NearbySearchRequest
{
    [JsonPropertyName("includedTypes")] public List<string> IncludedTypes { get; set; }
    [JsonPropertyName("maxResultCount")] public int MaxResultCount { get; set; }
    [JsonPropertyName("locationRestriction")] public LocationRestriction LocationRestriction { get; set; }
}
public class LocationRestriction { [JsonPropertyName("circle")] public SearchCircle Circle { get; set; } }
public class SearchCircle { [JsonPropertyName("center")] public RouteLatLng Center { get; set; } [JsonPropertyName("radius")] public double Radius { get; set; } }
public class NearbySearchResponse { [JsonPropertyName("places")] public List<PlaceResult> Places { get; set; } }
public class PlaceResult
{
    [JsonPropertyName("displayName")] public DisplayName DisplayName { get; set; }
    [JsonPropertyName("location")] public RouteLatLng Location { get; set; }
    [JsonPropertyName("rating")] public double Rating { get; set; }
}
public class DisplayName { [JsonPropertyName("text")] public string Text { get; set; } }
public class SpeedLimitsResponse
{
    [JsonPropertyName("speedLimits")]
    public List<SpeedLimitData> SpeedLimits { get; set; }
}

public class SpeedLimitData
{
    [JsonPropertyName("speedLimit")]
    public int SpeedLimit { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; }
}
public class SearchTextRequest
{
    [JsonPropertyName("textQuery")] public string TextQuery { get; set; }
    [JsonPropertyName("searchAlongRouteParameters")] public SearchAlongRouteParameters SearchAlongRouteParameters { get; set; }
}

public class SearchAlongRouteParameters
{
    [JsonPropertyName("polyline")] public RoutePolyline Polyline { get; set; }
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

public class MapPinTemplateSelector : DataTemplateSelector
{
    public DataTemplate RiderTemplate { get; set; }
    public DataTemplate DestinationTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is MapPinViewModel vm && vm.IsDestination) return DestinationTemplate;
        return RiderTemplate;
    }
}

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

    private bool _isTracking = false;
    private Location _lastKnownLocation; // Used primarily for UI/Map snapping
    private RiderPin _myPinVm;
    private Location _pendingDestination;
    private Polyline _activeRouteLine;

    private readonly ConcurrentDictionary<string, RiderPin> _riderViewModels = new();
    private readonly Random _randomColorGen = new();

    private bool _isSimulating = false;
    private int _autocompleteApiHits = 0;
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
    private DateTime _lastSpeedLimitFetch = DateTime.MinValue;
    private int _currentSpeedLimit = 0;

    public LobbyPage(SignalRService signalRService,  GroupDetailsDto groupDetails)
    {
        InitializeComponent();
        BindingContext = this;

        DeviceDisplay.Current.KeepScreenOn = Preferences.Default.Get("KeepScreenOn", false);

        _signalRService = signalRService;
        _rideCache = IPlatformApplication.Current?.Services.GetService<RideStateService>();
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
                if(_rideCache.CurrentSettings.GroupName != groupDetails.Settings.GroupName)
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
            AdminSettingsBtn.IsVisible = _amIAdmin;
            await InitializeLocalTrackingAsync();

            if (groupDetails != null)
            {
                ConvoyPin = groupDetails.JoinCode ?? "------";
                OnPropertyChanged(nameof(ConvoyPin));
                AdminPinCard.IsVisible = _amIAdmin;

                if (groupDetails.CurrentState == GroupState.DestinationSet)
                {
                    groupDetails.CurrentState = GroupState.NotNavigating;
                    await ChangeGroupState(GroupState.DestinationSet);
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
        _signalRService.LeadRouteUpdated -= OnLeadRouteUpdated;
        _signalRService.RouteDeviationAlert -= OnRouteDeviationAlert;
        _signalRService.MeetupPointSet -= OnMeetupPointSet;
        _signalRService.GroupSettingsUpdated -= OnSettingsPushedFromServer;

        if (_hwButtonService != null)
        {
            _hwButtonService.PttPressed -= OnHardwarePttPressed;
            _hwButtonService.PttReleased -= OnHardwarePttReleased;
        }

        _isTracking = false;
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
            AdminSettingsBtn.IsVisible = _amIAdmin;
            AdminPinCard.IsVisible = _amIAdmin;
            TabAdminBtn.IsVisible = _amIAdmin;

            _locationTracker?.UpdateRiderCount(Riders.Count(r => r.IsOnline));
        });
    }

    // --- LOCATION PROCESSING & TELEMETRY ---
    private async void OnLocalLocationPushedFromBackground(object sender, LocalLocationUpdate e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LocationDisabledOverlay.IsVisible = false;
            if (_myPinVm != null)
            {
                _myPinVm.Location = e.Location;
                _myPinVm.Speed = $"{Math.Round(e.SpeedMph * 1.60934)} kmph";
                _myPinVm.Heading = e.Heading;
            }
        });

        _lastKnownLocation = e.Location;

        if (groupDetails?.CurrentState == GroupState.Navigating)
        {
            await TrimRouteVisuals(e.Location);
            double speedKmh = e.SpeedMph * 1.60934; // Convert mph back to kmh for the telemetry engine
            await EvaluateEdgeTelemetry(e.Location, speedKmh);
            // NEW: Fire the Speed Limit Engine and Camera Physics
            _ = EvaluateSpeedLimitAsync(e.Location, speedKmh);
        }
    }
    private async Task EvaluateSpeedLimitAsync(Location loc, double currentSpeedKmh)
    {
        if (!Preferences.Default.Get("Map_SpeedLimits", true))
        {
            MainThread.BeginInvokeOnMainThread(() => SpeedLimitBadge.IsVisible = false);
            return;
        }

        // To protect billing and avoid rate limits, we fetch the limit every 5 minutes.
        // In a production app, you might also trigger this via a background Geofence when the road name changes!
        if ((DateTime.Now - _lastSpeedLimitFetch).TotalMinutes > 5)
        {
            try
            {
                // Reset limit while fetching to prevent showing stale highway limits on small dirt roads
                _currentSpeedLimit = 0;

                string latStr = loc.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lngStr = loc.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // The Roads API allows you to pass raw coordinates directly to the 'path' parameter
                var requestUri = $"https://roads.googleapis.com/v1/speedLimits?path={latStr},{lngStr}&units=KPH&key={_googleApiKey}";

                var response = await _httpClient.GetAsync(requestUri);
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<SpeedLimitsResponse>(responseBody);

                    if (result?.SpeedLimits != null && result.SpeedLimits.Any())
                    {
                        int fetchedLimit = result.SpeedLimits.First().SpeedLimit;
                        if (fetchedLimit > 0)
                        {
                            _currentSpeedLimit = fetchedLimit;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Speed Limit API Error: {ex.Message}");
            }

            _lastSpeedLimitFetch = DateTime.Now;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_currentSpeedLimit > 0)
            {
                SpeedLimitBadge.IsVisible = true;
                SpeedLimitLabel.Text = _currentSpeedLimit.ToString();

                // 15% tolerance rule for red text
                if (currentSpeedKmh > _currentSpeedLimit * 1.15)
                {
                    MySpeedLabel.TextColor = Colors.Red;
                    SpeedLimitBadge.Stroke = Colors.Red;
                }
                else
                {
                    MySpeedLabel.TextColor = Colors.DodgerBlue;
                    SpeedLimitBadge.Stroke = Colors.Gray;
                }
            }
        });
    }

    private async Task TrimRouteVisuals(Location currentLocation)
    {
        if (_activeRouteLine == null || _rideCache.ActiveDestination == null || _rideCache.CurrentRoutePoints.Count < 2) return;

        // --- Calculate cumulative distance using the CACHE ---
        if (_rideCache.LastOdometerLocation != null)
        {
            double stepDistance = Location.CalculateDistance(_rideCache.LastOdometerLocation, currentLocation, DistanceUnits.Kilometers);
            if (stepDistance > 0 && stepDistance < 20) // FIX: Increased from 1 to 20 to allow for long straight highway segments!
            {
                _rideCache.CumulativeDistanceKm += stepDistance;
            }
        }
        _rideCache.LastOdometerLocation = currentLocation;

        // --- LOCAL TELEMETRY & PITSTOP TRACKING (Using Cache) ---
        if (currentLocation?.Speed != null)
        {
            double speedKmh = (currentLocation?.Speed ?? 0) * 3.6;
            if (speedKmh > _rideCache.MaxSpeedKmh) _rideCache.MaxSpeedKmh = speedKmh;

            if (speedKmh < 2) // Stopped
            {
                if (_rideCache.LastStopTime == null) _rideCache.LastStopTime = DateTime.Now;
            }
            else // Moving
            {
                if (_rideCache.LastStopTime != null)
                {
                    _rideCache.TotalStoppedTime += (DateTime.Now - _rideCache.LastStopTime.Value);
                    _rideCache.LastStopTime = null;
                }
            }
        }

        double minDistance = double.MaxValue;
        int closestIndex = 0;

        int searchRange = Math.Min(20, _rideCache.CurrentRoutePoints.Count);
        for (int i = 0; i < searchRange; i++)
        {
            double dist = Location.CalculateDistance(currentLocation, _rideCache.CurrentRoutePoints[i], DistanceUnits.Kilometers);
            if (dist < minDistance)
            {
                minDistance = dist;
                closestIndex = i;
            }
        }

        // OFF ROUTE DETECTION (> 100 meters)
        if (minDistance > 0.1)
        {
            if ((DateTime.Now - _rideCache.LastRerouteTime).TotalSeconds > 15)
            {
                _rideCache.LastRerouteTime = DateTime.Now;

                _ = Task.Run(async () => {
                    string newPolyline = await CalculateAndDrawRoute(currentLocation, _rideCache.ActiveDestination, _rideCache.ActiveMeetupPoint);

                    if (!string.IsNullOrEmpty(newPolyline))
                    {
                        var settings = await _signalRService.GetGroupSettings(GroupNameLabel.Text);
                        if (settings != null && settings.EnableDynamicRouting)
                        {
                            if (_amIAdmin)
                            {
                                await _signalRService.BroadcastLeadRoute(GroupNameLabel.Text, newPolyline);
                            }
                            else
                            {
                                await _signalRService.ReportRouteDeviation(GroupNameLabel.Text, _myName);
                            }
                        }
                    }
                });
            }
            return;
        }

        // --- THE MY RIDE UI UPDATER ---
        MainThread.BeginInvokeOnMainThread(() => {
            try
            {
                if (_myPinVm != null) MySpeedLabel.Text = _myPinVm.Speed;

                // FIX: Calculate distance along the polyline path instead of a straight line!
                double distLeft = 0;
                var currentPoints = _rideCache.CurrentRoutePoints;
                if (currentPoints.Count > 1)
                {
                    distLeft += Location.CalculateDistance(currentLocation, currentPoints[0], DistanceUnits.Kilometers);
                    for (int j = 0; j < currentPoints.Count - 1; j++)
                    {
                        distLeft += Location.CalculateDistance(currentPoints[j], currentPoints[j + 1], DistanceUnits.Kilometers);
                    }
                }
                else
                {
                    distLeft = Location.CalculateDistance(currentLocation, _rideCache.ActiveDestination, DistanceUnits.Kilometers);
                }

                MyDistanceLabel.Text = $"{Math.Round(distLeft, 1)} km";
                MyTotalTraveledLabel.Text = $"{Math.Round(_rideCache.CumulativeDistanceKm, 1)} km";

                // Total Route will now remain highly stable
                MyTotalRouteLabel.Text = $"{Math.Round(_rideCache.CumulativeDistanceKm + distLeft, 1)} km";

                // NEW: Dynamic ETA Math
                double currentSpeed = (currentLocation?.Speed ?? 0) * 3.6;
                double movingAvg = Math.Max(currentSpeed, 40); // Assume min 40km/h average if stuck in traffic
                double hoursLeft = distLeft / movingAvg;
                DateTime eta = DateTime.Now.AddHours(hoursLeft);

                MyEtaLabel.Text = $"ETA {eta:HH:mm}";
                MyEtaLabel.IsVisible = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Stats Update Error: {ex.Message}"); }
        });

        if (closestIndex > 0)
        {
            int logicalPointsToRemove = Math.Min(closestIndex, _rideCache.CurrentRoutePoints.Count - 2);
            if (logicalPointsToRemove > 0)
            {
                _rideCache.CurrentRoutePoints.RemoveRange(0, logicalPointsToRemove);
            }
        }
    }

    private async Task EvaluateEdgeTelemetry(Location myLoc, double mySpeedKmh)
    {
        var settings = _rideCache.CurrentSettings;
        if (settings == null) return;

        bool amILead = _rideCache.MyRole == "Lead" || (_amIAdmin && string.IsNullOrEmpty(settings.LeadRiderGoogleId));

        var activeSpeeds = _rideCache.OtherRiderSpeeds.Values.Where(s => s > 10).ToList();
        if (activeSpeeds.Count > 1 && mySpeedKmh > 10)
        {
            double avgGroupSpeed = activeSpeeds.Average();
            if (mySpeedKmh > avgGroupSpeed + 30)
            {
                if ((DateTime.Now - _rideCache.LastSpeedAlert).TotalMinutes > 10)
                {
                    _rideCache.LastSpeedAlert = DateTime.Now;
                    _ = TextToSpeech.Default.SpeakAsync("Warning: You are riding significantly faster than the group average.");
                }
            }
        }

        if (amILead)
        {
            if (_rideCache.ActiveDestination != null && (DateTime.Now - _rideCache.LastArrivalAlert).TotalMinutes > 15)
            {
                double distToDest = Location.CalculateDistance(myLoc, _rideCache.ActiveDestination, DistanceUnits.Kilometers) * 1000;
                double arrivalThreshold = settings.ArrivalGeofenceMeters > 0 ? settings.ArrivalGeofenceMeters : 1000;

                if (distToDest < arrivalThreshold)
                {
                    _rideCache.LastArrivalAlert = DateTime.Now;
                    await _signalRService.SendArrivalAlert(GroupNameLabel.Text);
                }
            }

            double pitstopIntervalKm = settings.PitstopDistanceMeters / 1000.0;
            if (pitstopIntervalKm > 0 && (_rideCache.CumulativeDistanceKm - _rideCache.LastGroupPitstopKm) >= pitstopIntervalKm)
            {
                _rideCache.LastGroupPitstopKm = _rideCache.CumulativeDistanceKm;
                _ = TextToSpeech.Default.SpeakAsync($"You have traveled {Math.Round(_rideCache.CumulativeDistanceKm)} kilometers. Consider a rest stop.");
                await _signalRService.SendPitstopReminder(GroupNameLabel.Text, _rideCache.CumulativeDistanceKm);
            }

            if (settings.SplinterWarningDistanceMeters > 0 && Riders.Count(x=> x.IsOnline) > 1 && (DateTime.Now - _rideCache.LastSplinterAlert).TotalMinutes > 5)
            {
                double maxDistMeters = 0;
                foreach (var riderLoc in _rideCache.OtherRiderLocations.Values)
                {
                    double d = Location.CalculateDistance(myLoc, riderLoc, DistanceUnits.Kilometers) * 1000;
                    if (d > maxDistMeters) maxDistMeters = d;
                }

                if (maxDistMeters > settings.SplinterWarningDistanceMeters)
                {
                    _rideCache.LastSplinterAlert = DateTime.Now;
                    await _signalRService.SendSplinterWarning(GroupNameLabel.Text);
                }
            }
        }
        else
        {
            if (settings.MaxLagDistanceMeters > 0 && (DateTime.Now - _rideCache.LastLagAlert).TotalMinutes > 3)
            {
                string leadId = string.IsNullOrEmpty(settings.LeadRiderGoogleId) ? groupDetails?.AdminGoogleId : settings.LeadRiderGoogleId;

                if (!string.IsNullOrEmpty(leadId) && _rideCache.OtherRiderLocations.TryGetValue(leadId, out var leadLoc))
                {
                    double distToLead = Location.CalculateDistance(myLoc, leadLoc, DistanceUnits.Kilometers) * 1000;
                    if (distToLead > settings.MaxLagDistanceMeters)
                    {
                        _rideCache.LastLagAlert = DateTime.Now;
                        await _signalRService.SendLagWarning(GroupNameLabel.Text, _myName, distToLead, false);
                    }
                }
            }
        }
    }

    private async Task<RideSummary> ProcessAndSaveRideTelemetry(string groupName)
    {
        try
        {
            var summary = new RideSummary
            {
                Id = Guid.NewGuid().ToString(),
                RideDate = DateTime.Now,
                GroupName = groupName,
                DestinationName = _rideCache.ActiveDestinationName ?? "Unknown Destination",
                TotalDistanceKm = Math.Round(_rideCache.CumulativeDistanceKm, 2),
                TopSpeedKmh = Math.Round(_rideCache.MaxSpeedKmh, 1),
                TotalElapsedTime = DateTime.Now - _rideCache.RideStartTime,
                StoppedTime = _rideCache.TotalStoppedTime,
            };

            summary.MovingTime = summary.TotalElapsedTime - summary.StoppedTime;

            if (summary.MovingTime.TotalHours > 0)
            {
                summary.AverageMovingSpeedKmh = Math.Round(summary.TotalDistanceKm / summary.MovingTime.TotalHours, 1);
            }

            await LocalRideLogger.SaveRideAsync(summary);

            _rideCache.HardResetAll();

            // THE FIX: Return the summary so the UI can display it!
            return summary;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving ride: {ex.Message}");
            return null;
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
            request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var routeResult = JsonSerializer.Deserialize<RoutesResponse>(await response.Content.ReadAsStringAsync());
            var mainRoute = routeResult?.Routes?.FirstOrDefault();

            if (mainRoute != null)
            {
                var decodedPoints = _rideCache.CurrentRoutePoints = DecodeGooglePolyline(mainRoute.Polyline.EncodedPolyline);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (isMainRoute)
                    {
                        // Safely remove only the main route (leaving the spiderweb intact!)
                        if (_activeRouteLine != null) LiveMap.MapElements.Remove(_activeRouteLine);

                        _activeRouteLine = new Polyline { StrokeColor = routeColor ?? Colors.DodgerBlue, StrokeWidth = 8 };
                        foreach (var coord in _rideCache.CurrentRoutePoints) _activeRouteLine.Geopath.Add(coord);
                        LiveMap.MapElements.Add(_activeRouteLine);
                    }
                    else
                    {
                        // Draw a secondary spiderweb route
                        var otherLine = new Polyline { StrokeColor = routeColor ?? Colors.MediumPurple, StrokeWidth = 5 };
                        foreach (var coord in decodedPoints) otherLine.Geopath.Add(coord);

                        LiveMap.MapElements.Add(otherLine);
                        _otherRiderRoutes.Add(otherLine);

                        // Drop a label pin in the middle of their route line!
                        if (!string.IsNullOrEmpty(riderName) && decodedPoints.Count > 0)
                        {
                            int midIndex = decodedPoints.Count / 2;
                            var labelPin = new Pin
                            {
                                Location = decodedPoints[midIndex],
                                Label = $"{riderName}'s Route",
                                Type = PinType.Generic
                            };
                            LiveMap.Pins.Add(labelPin);
                            _otherRiderRoutePins.Add(labelPin);
                        }
                    }
                });

                return mainRoute.Polyline.EncodedPolyline;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Routing Error: {ex.Message}"); }
        return null;
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

    private void OnLeadRouteUpdated(string encodedPolyline)
    {
        _rideCache.CurrentRoutePoints = DecodeGooglePolyline(encodedPolyline);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var oldLines = LiveMap.MapElements.OfType<Polyline>().ToList();
            foreach (var line in oldLines) LiveMap.MapElements.Remove(line);

            _activeRouteLine = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 8 };
            foreach (var coord in _rideCache.CurrentRoutePoints) _activeRouteLine.Geopath.Add(coord);
            LiveMap.MapElements.Add(_activeRouteLine);

            _ = TextToSpeech.Default.SpeakAsync("Map synced with Lead rider.");
        });
    }

    private void OnRouteDeviationAlert(string userName)
    {
        MainThread.BeginInvokeOnMainThread(() => _ = TextToSpeech.Default.SpeakAsync($"{userName} has diverted from the route."));
    }
    private async Task<List<Location>> GetRoutePointsOnlyAsync(Location origin, Location dest)
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
            if (response.IsSuccessStatusCode)
            {
                var routeResult = JsonSerializer.Deserialize<RoutesResponse>(await response.Content.ReadAsStringAsync());
                var mainRoute = routeResult?.Routes?.FirstOrDefault();
                if (mainRoute != null)
                {
                    return DecodeGooglePolyline(mainRoute.Polyline.EncodedPolyline);
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Silent Route Fetch Error: {ex.Message}"); }
        return new List<Location>();
    }
    private async void OnGenerateMeetupClicked(object sender, EventArgs e)
    {
        if (_rideCache.ActiveDestination == null || _lastKnownLocation == null)
        {
            await DisplayAlert("Hold Up", "You must set a Destination before generating a meetup point.", "OK");
            return;
        }

        var riderLocations = _rideCache.OtherRiderLocations.Values.ToList();
        if (riderLocations.Count == 0)
        {
            await DisplayAlert("No Riders", "There are no other online riders to meet up with.", "OK");
            return;
        }

        ShowLoading("Calculating Convergence Point...");
        try
        {
            // 1. Fetch Lead's Route (The Baseline)
            var leadRoute = await GetRoutePointsOnlyAsync(_lastKnownLocation, _rideCache.ActiveDestination);
            if (leadRoute.Count == 0) return;

            // 2. Fetch routes for all other riders IN PARALLEL for speed
            var routeTasks = new List<Task<List<Location>>>();
            foreach (var loc in riderLocations)
            {
                routeTasks.Add(GetRoutePointsOnlyAsync(loc, _rideCache.ActiveDestination));
            }

            var otherRoutes = await Task.WhenAll(routeTasks);

            // 3. Find the convergence point (Trace backwards from Destination)
            Location meetupPoint = leadRoute.Last(); // Default to destination

            // We iterate backward from the destination. The points will match everyone's route 
            // until the geographical paths split. 
            for (int i = leadRoute.Count - 1; i >= 0; i--)
            {
                Location pt = leadRoute[i];
                bool sharedByAll = true;

                foreach (var route in otherRoutes)
                {
                    if (route == null || route.Count == 0) continue;

                    // Google's polyline nodes won't match to the exact 6th decimal place.
                    // We use a 100-meter tolerance radius to check if this road is shared.
                    bool foundNear = route.Any(rPt => Location.CalculateDistance(pt, rPt, DistanceUnits.Kilometers) < 0.1);
                    if (!foundNear)
                    {
                        sharedByAll = false;
                        break;
                    }
                }

                if (sharedByAll)
                {
                    // This point is shared by everyone.
                    // Keep moving backward to find the earliest possible shared merge point.
                    meetupPoint = pt;
                }
                else
                {
                    // Divergence found! The PREVIOUS 'meetupPoint' was the last shared point.
                    break;
                }
            }

            // 4. Broadcast the computed Meetup Point to the convoy
            await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, meetupPoint.Latitude, meetupPoint.Longitude);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not calculate meetup: {ex.Message}", "OK");
        }
        finally
        {
            HideLoading();
        }
    }

    // =====================================================================
    // --- UPDATED: TRIGGER THE SPIDERWEB AFTER MEETUP IS SET ---
    // =====================================================================
    private async void OnMeetupPointSet(double lat, double lng)
    {
        _rideCache.ActiveMeetupPoint = new Location(lat, lng);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LiveMap.Pins.Add(new Pin { Label = "Meetup Point", Location = _rideCache.ActiveMeetupPoint, Type = PinType.Generic });
        });

        if (_lastKnownLocation != null && _rideCache.ActiveDestination != null)
        {
            // 1. Calculate my own personal route to the meetup point
            await CalculateAndDrawRoute(_lastKnownLocation, _rideCache.ActiveDestination, _rideCache.ActiveMeetupPoint);

            // 2. If I am the Lead, download and draw the spiderweb!
            bool amILead = _rideCache.MyRole == "Lead" || (_amIAdmin && string.IsNullOrEmpty(_rideCache.CurrentSettings?.LeadRiderGoogleId));

            if (amILead)
            {
                MainThread.BeginInvokeOnMainThread(() => ClearOtherRiderRoutes());

                var random = new Random();
                foreach (var rider in _rideCache.OtherRiderLocations)
                {
                    // Generate a highly visible, dark random color for contrast
                    Color riderColor = Color.FromRgb((byte)random.Next(20, 200), (byte)random.Next(20, 200), (byte)random.Next(20, 200));

                    // Draw it quietly in the background
                    _ = CalculateAndDrawRoute(
                        origin: rider.Value,
                        dest: _rideCache.ActiveDestination,
                        meetup: _rideCache.ActiveMeetupPoint,
                        routeColor: riderColor,
                        riderName: rider.Key,
                        isMainRoute: false);
                }
            }
        }
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

        if (sender == TabActionsBtn && groupDetails.CurrentState > GroupState.DestinationSet) 
        { 
            TabActionsBtn.BackgroundColor = Colors.DodgerBlue; 
            TabActionsBtn.TextColor = Colors.White; 
            DrawerActionsTab.IsVisible = true; 
        }
        else if (sender == TabStatsBtn && groupDetails.CurrentState > GroupState.DestinationSet) { TabStatsBtn.BackgroundColor = Colors.DodgerBlue; TabStatsBtn.TextColor = Colors.White; DrawerStatsTab.IsVisible = true; _ = RefreshTelemetryData(); }
        else if (sender == TabAdminBtn && groupDetails.CurrentState > GroupState.DestinationSet) { TabAdminBtn.BackgroundColor = Colors.DodgerBlue; TabAdminBtn.TextColor = Colors.White; DrawerAdminTab.IsVisible = true; }
        else if (sender == TabMapSettingsBtn) { TabMapSettingsBtn.BackgroundColor = Colors.DodgerBlue; TabMapSettingsBtn.TextColor = Colors.White; DrawerMapSettingsTab.IsVisible = true; }
        if (ActionDrawer.TranslationY >= (_drawerFullHeight - _drawerPeekHeight) - 10)
            ActionDrawer.TranslateTo(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
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
                case GroupState.DestinationSet:
                    OnDestinationSet(groupDetails.DestLat, groupDetails.DestLng, groupDetails.DestName);
                    _isSelectingLocation = true;
                    DestinationSearchBar.Text = groupDetails.DestName;
                    AdminInstructionBanner.IsVisible = false;
                    ConfirmDestButton.IsVisible = false;
                    ResetDestButton.IsVisible = true;
                    _isSelectingLocation = false;
                    ActionDrawer.IsVisible = true;
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;
                    break;
                case GroupState.NotNavigating:
                case GroupState.Completed:
                    ActionDrawer.IsVisible = false;
                    FloatingMapControls.IsVisible = false;
#if DEBUG
                    SimSpeedFrame.IsVisible = false;
#endif
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
#if DEBUG
                    SimSpeedFrame.IsVisible = true;
#endif
                    ActionDrawer.TranslationY = _drawerFullHeight - _drawerPeekHeight;

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
#if DEBUG
                    SimSpeedFrame.IsVisible = false;
#endif

                    string context = newState == GroupState.PausedBreak ? "for a break" :
                                     newState == GroupState.PausedHazard ? "due to a hazard" :
                                     "for mechanical repairs";

                    string spokenReason = string.IsNullOrEmpty(reason) ? context : reason;
                    _ = TextToSpeech.Default.SpeakAsync($"Navigation paused by {triggerUser} {spokenReason}. Tracking suspended.");

                    ActionDrawer.TranslateToAsync(0, _drawerFullHeight * 0.4, 250, Easing.CubicOut);
                    //OnDrawerTabClicked(TabStatsBtn, EventArgs.Empty);
                    break;
            }
        });
    }

    // --- EVENT TRIGGERS ---
    private async void OnDestinationSet(double destLat, double destLng, string destName)
    {
        ShowLoading("Drawing Route Preview...");
        try
        {
            _rideCache.ResetNavigationState();
            _rideCache.ActiveDestination = new Location(destLat, destLng);
            _rideCache.ActiveDestinationName = destName;

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
        finally { HideLoading(); }
    }

    private async void OnNavigationStarted(double destLat, double destLng, string destName, bool isSyncRequired = false)
    {
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
            MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());

#if DEBUG
            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any() && !_isSimulating)
            {
                _ = SimulateMovementAlongRouteAsync();
            }
#endif
        }
        if (!isSyncRequired)
            _ = TextToSpeech.Default.SpeakAsync($"Navigation started to {destName}. Ride safe!");
    }

    private async void OnNavigationCompleted(string adminName)
    {
        string currentGroupName = GroupNameLabel.Text;

        // 1. GET THE SUMMARY BACK FROM THE BACKGROUND THREAD!
        var finalSummary = await Task.Run(async () =>
        {
            return await ProcessAndSaveRideTelemetry(currentGroupName);
        });

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
            PendingDestinationFrame.IsVisible = false;
            ConfirmDestButton.IsVisible = true;
            ResetDestButton.IsVisible = false;
            DestinationSearchBar.IsReadOnly = false;
            DestinationSearchBar.Text = string.Empty;

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
            await _signalRService.StartGroupNavigation(GroupNameLabel.Text, _rideCache.ActiveDestination.Latitude, _rideCache.ActiveDestination.Longitude, PendingDestinationLabel.Text);
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
        if (points == null)
        {
            if (MapPins.Count == 0) return;
            points = MapPins.Select(p => p.Location).ToList();
        }

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
    private async void OnRefreshTelemetryClicked(object sender, EventArgs e)
    {
        await RefreshTelemetryData();
    }

    // Include your remaining hardware hooks, search UI logic, mapping interactions, PTT methods etc below exactly as they were...

    // (Omitted purely to save response space, but you keep your existing Search/PiP/Hardware/Alerts logic here unchanged)

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

            if (!calledFromNavStart && _rideCache.ActiveDestination != null)
            {
                var currentLoc = await Geolocation.Default.GetLastKnownLocationAsync() ?? _lastKnownLocation;
                UpdateDestinationPin(_rideCache.ActiveDestination, DestinationSearchBar.Text ?? PendingDestinationLabel.Text ?? "Selected Destination");
                await CalculateAndDrawRoute(currentLoc, _rideCache.ActiveDestination);
                if (groupDetails.CurrentState < GroupState.Navigating)
                {
                    MainThread.BeginInvokeOnMainThread(() => FitMapToBounds([currentLoc, _rideCache.ActiveDestination]));
                }
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => FitMapToBounds());
            }
        }
    }
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

        await ChangeGroupState(GroupState.DestinationSet);

        OnTabClicked(TabRoster, EventArgs.Empty);

        await _signalRService.SetGroupDestination(GroupNameLabel.Text, _pendingDestination.Latitude, _pendingDestination.Longitude, destName);
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

                await ShowPoisTemporarilyAsync(new List<string> { "gas_station" }, "⛽");
            }
            else if (alertType == "Rest")
            {
                SensoryAlertOverlay.BackgroundColor = Colors.DodgerBlue;
                AlertTitleLabel.Text = "REST STOP";
                AlertIconLabel.Text = "☕";
                durationSeconds = 5;
                voiceMessage = $"{senderName} requested a rest stop. Prepare to pull over soon.";

                await ShowPoisTemporarilyAsync(new List<string> { "restaurant", "cafe" }, "🍽️");
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
            if (_rideCache.CurrentRoutePoints != null && _rideCache.CurrentRoutePoints.Any() && !_isSimulating)
            {
                _ = SimulateMovementAlongRouteAsync();
            }
#endif
        }

        _ = TextToSpeech.Default.SpeakAsync($"Resuming Navigation to {groupDetails.DestName}. Ride safe!");
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
        double speedKmh = 0;

        // 1. CALCULATE SPEED & WRITE TO EDGE CACHE!
        if (_rideCache.OtherRiderLocations.TryGetValue(riderId, out var oldLoc) &&
            _riderLastUpdateTimes.TryGetValue(riderId, out var lastTime))
        {
            double distKm = Location.CalculateDistance(oldLoc, newLoc, DistanceUnits.Kilometers);
            double hours = (now - lastTime).TotalHours;

            if (hours > 0)
            {
                speedKmh = distKm / hours;
                // Ignore crazy GPS jumps (e.g., > 250 km/h)
                if (speedKmh > 250) speedKmh = _rideCache.OtherRiderSpeeds.GetValueOrDefault(riderId, 0);
            }
        }

        // Store the fresh data into the Singleton Cache for the Telemetry Engine!
        _rideCache.OtherRiderLocations[riderId] = newLoc;
        _rideCache.OtherRiderSpeeds[riderId] = speedKmh;
        _riderLastUpdateTimes[riderId] = now;

        // 2. UPDATE THE MAP UI
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_riderViewModels.TryGetValue(riderId, out var existingVm))
            {
                existingVm.Location = newLoc;
                existingVm.Heading = heading;
                existingVm.Speed = speedKmh > 1 ? $"{Math.Round(speedKmh)} km/h" : "Stopped";

                AnimatePinMovement(existingVm, newLoc, heading, 1000);
            }
            else
            {
                Color randomColor = Color.FromRgb((byte)_randomColorGen.Next(50, 230), (byte)_randomColorGen.Next(50, 230), (byte)_randomColorGen.Next(50, 230));
                var newVm = new RiderPin(MapPinClicked)
                {
                    Username = riderId,
                    Speed = "Active",
                    Location = newLoc,
                    Heading = heading,
                    PinColor = randomColor,
                    ZIndex = 50F
                };

                _riderViewModels.TryAdd(riderId, newVm);
                MapPins.Add(newVm);
            }
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
    private async Task SimulateMovementAlongRouteAsync()
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count == 0) return;

        // FIX: Prevent multiple overlapping simulations if they stop/start the route
        if (_isSimulating) return;

        await Task.Delay(2000);
        _isSimulating = true;

        if (_locationTracker != null) _locationTracker.IsSimulating = true;

        // FIX: Freeze a copy of the route! 
        // This prevents the Trimmer from deleting points out from under the loop's index!
        var simulationPath = _rideCache.CurrentRoutePoints.ToList();
        int currentIndex = 0;

        while (currentIndex < simulationPath.Count)
        {
            if (groupDetails.CurrentState != GroupState.Navigating || !_isSimulating)
            {
                _isSimulating = false;
                break;
            }

            double speedKmh = _simSpeedKmh;

            if (speedKmh == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            // Draw from the frozen path, so we never skip a coordinate
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

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_myPinVm != null)
                {
                    _myPinVm.Location = point;
                    _myPinVm.Speed = $"{Math.Round(speedKmh)} km/h";
                    _myPinVm.Heading = fakeHeading;

                    AnimatePinMovement(_myPinVm, point, fakeHeading, (uint)delayMs);
                }
            });

            _lastKnownLocation = point;

            // --- THE FIX: Pass Simulation through the Gatekeeper! ---
            if (_rideCache.ShouldBroadcastLocation(point, speedKmh))
            {
                _rideCache.LastBroadcastLocation = point;
                await _signalRService.UpdateLocation(GroupNameLabel.Text, _myName, point.Latitude, point.Longitude, fakeHeading);
            }
            await TrimRouteVisuals(point);
            await EvaluateEdgeTelemetry(point, speedKmh);

            // NEW: Fire the Speed Limit Engine and Camera Physics
            _ = EvaluateSpeedLimitAsync(point, speedKmh);

            await Task.Delay(delayMs);
            currentIndex++;
        }

        _isSimulating = false;
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
                string encodedPath = EncodeLocationList(upcomingPath);

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
    }
    private void OnMapSettingChanged(object sender, ToggledEventArgs e)
    {
        Preferences.Default.Set("Map_Traffic", TrafficSwitch.IsToggled);
        Preferences.Default.Set("Map_SpeedLimits", SpeedLimitSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoZoom", AutoZoomSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoTilt", AutoTiltSwitch.IsToggled);

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
    // =====================================================================
    // --- NEW: POLYLINE ENCODER FOR SEARCH-ALONG-ROUTE ---
    // =====================================================================
    private string EncodeLocationList(List<Location> points)
    {
        var str = new System.Text.StringBuilder();
        int prevLat = 0, prevLng = 0;
        foreach (var point in points)
        {
            int lat = (int)Math.Round(point.Latitude * 1e5);
            int lng = (int)Math.Round(point.Longitude * 1e5);
            EncodeDifference(str, lat - prevLat);
            EncodeDifference(str, lng - prevLng);
            prevLat = lat;
            prevLng = lng;
        }
        return str.ToString();
    }

    private void EncodeDifference(System.Text.StringBuilder str, int diff)
    {
        int shifted = diff << 1;
        if (diff < 0) shifted = ~shifted;
        while (shifted >= 0x20)
        {
            str.Append((char)((0x20 | (shifted & 0x1f)) + 63));
            shifted >>= 5;
        }
        str.Append((char)(shifted + 63));
    }
}