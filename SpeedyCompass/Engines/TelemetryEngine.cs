using System.Text.Json;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Engines;

public class RiderRelativeStatus
{
    public double SpeedKmh { get; set; }
    public string SpeedStr { get; set; }
    public string StatusStr { get; set; }
    public Color StatusColor { get; set; }
    public Location InterpolatedLocation { get; set; }
}
public interface ITelemetryEngine
{
    Task EvaluateSpeedLimitAsync(Location loc, double currentSpeedKmh, Action<int, bool> updateUiCallback);
    Task EvaluateEdgeTelemetryAsync(Location myLoc, double mySpeedKmh, string myName, string groupName, bool amIAdmin);
    Task<RideSummary> ProcessAndSaveRideTelemetryAsync(string groupName);
    public RiderRelativeStatus CalculateRiderStatus(string riderId, Location newLoc, Location myLoc, RideStateService rideCache, GroupSettingsDto settings);
}

public class TelemetryEngine : ITelemetryEngine
{
    private readonly RideStateService _rideCache;
    private readonly SignalRService _signalRService;
    private readonly IVoiceCopilotEngine _voiceEngine;
    private readonly HttpClient _httpClient;

    private DateTime _lastSpeedLimitFetch = DateTime.MinValue;
    private int _currentSpeedLimit = 0;
    private readonly string _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk";

    public TelemetryEngine(RideStateService rideCache, SignalRService signalRService, IVoiceCopilotEngine voiceEngine)
    {
        _rideCache = rideCache;
        _signalRService = signalRService;
        _voiceEngine = voiceEngine;
        _httpClient = new HttpClient();
    }

    // =====================================================================
    // --- 1. SPEED LIMIT API ENGINE ---
    // =====================================================================
    public async Task EvaluateSpeedLimitAsync(Location loc, double currentSpeedKmh, Action<int, bool> updateUiCallback)
    {
        if (!Preferences.Default.Get("Map_SpeedLimits", true))
        {
            updateUiCallback?.Invoke(0, false);
            return;
        }

        if ((DateTime.Now - _lastSpeedLimitFetch).TotalMinutes > 5)
        {
            try
            {
                _currentSpeedLimit = 0;
                string latStr = loc.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lngStr = loc.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var requestUri = $"https://roads.googleapis.com/v1/speedLimits?path={latStr},{lngStr}&units=KPH&key={_googleApiKey}";
                var response = await _httpClient.GetAsync(requestUri);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<SpeedLimitsResponse>(responseBody);

                    if (result?.SpeedLimits != null && result.SpeedLimits.Any())
                    {
                        int fetchedLimit = result.SpeedLimits.First().SpeedLimit;
                        if (fetchedLimit > 0) _currentSpeedLimit = fetchedLimit;
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Speed Limit API Error: {ex.Message}"); }

            _lastSpeedLimitFetch = DateTime.Now;
        }

        if (_currentSpeedLimit > 0)
        {
            bool isSpeeding = currentSpeedKmh > (_currentSpeedLimit * 1.15); // 15% tolerance
            updateUiCallback?.Invoke(_currentSpeedLimit, isSpeeding);
        }
    }

    // =====================================================================
    // --- 2. EDGE TELEMETRY & WARNINGS ENGINE ---
    // =====================================================================
    public async Task EvaluateEdgeTelemetryAsync(Location myLoc, double mySpeedKmh, string myName, string groupName, bool amIAdmin)
    {
        var settings = _rideCache.CurrentSettings;
        if (settings == null) return;

        bool amILead = _rideCache.MyRole == "Lead" || (amIAdmin && string.IsNullOrEmpty(settings.LeadRiderGoogleId));

        // 1. Group Average Speed Check
        var activeSpeeds = _rideCache.OtherRiderSpeeds.Values.Where(s => s > 10).ToList();
        if (activeSpeeds.Count > 1 && mySpeedKmh > 10)
        {
            double avgGroupSpeed = activeSpeeds.Average();
            if (mySpeedKmh > avgGroupSpeed + 30 && (DateTime.Now - _rideCache.LastSpeedAlert).TotalMinutes > 10)
            {
                _rideCache.LastSpeedAlert = DateTime.Now;
                _voiceEngine.Speak("Warning: You are riding significantly faster than the group average.");
            }
        }

        if (amILead)
        {
            // 2. Arrival Alert
            if (_rideCache.ActiveDestination != null && (DateTime.Now - _rideCache.LastArrivalAlert).TotalMinutes > 15)
            {
                double distToDest = Location.CalculateDistance(myLoc, _rideCache.ActiveDestination, DistanceUnits.Kilometers) * 1000;
                double arrivalThreshold = settings.ArrivalGeofenceMeters > 0 ? settings.ArrivalGeofenceMeters : 1000;

                if (distToDest < arrivalThreshold)
                {
                    _rideCache.LastArrivalAlert = DateTime.Now;
                    await _signalRService.SendArrivalAlert(groupName);
                }
            }

            // 3. Pitstop Reminder
            double pitstopIntervalKm = settings.PitstopDistanceMeters / 1000.0;
            if (pitstopIntervalKm > 0 && (_rideCache.CumulativeDistanceKm - _rideCache.LastGroupPitstopKm) >= pitstopIntervalKm)
            {
                _rideCache.LastGroupPitstopKm = _rideCache.CumulativeDistanceKm;
                _voiceEngine.Speak($"You have traveled {Math.Round(_rideCache.CumulativeDistanceKm)} kilometers. Consider a rest stop.");
                await _signalRService.SendPitstopReminder(groupName, _rideCache.CumulativeDistanceKm);
            }

            // 4. Splinter Warning (Group stretching too far)
            int onlineRiders = _rideCache.OtherRiderLocations.Count(x => true); // In a real app, track online status in cache
            if (settings.SplinterWarningDistanceMeters > 0 && onlineRiders > 0 && (DateTime.Now - _rideCache.LastSplinterAlert).TotalMinutes > 5)
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
                    await _signalRService.SendSplinterWarning(groupName);
                }
            }
        }
        else
        {
            // 5. Lagging Alert (Non-Leads falling behind)
            if (settings.MaxLagDistanceMeters > 0 && (DateTime.Now - _rideCache.LastLagAlert).TotalMinutes > 3)
            {
                string leadId = string.IsNullOrEmpty(settings.LeadRiderGoogleId) ? "" : settings.LeadRiderGoogleId;

                if (!string.IsNullOrEmpty(leadId) && _rideCache.OtherRiderLocations.TryGetValue(leadId, out var leadLoc))
                {
                    double distToLead = Location.CalculateDistance(myLoc, leadLoc, DistanceUnits.Kilometers) * 1000;
                    if (distToLead > settings.MaxLagDistanceMeters)
                    {
                        _rideCache.LastLagAlert = DateTime.Now;
                        await _signalRService.SendLagWarning(groupName, myName, distToLead, false);
                    }
                }
            }
        }
    }

    // =====================================================================
    // --- 3. END OF RIDE PROCESSOR ---
    // =====================================================================
    public async Task<RideSummary> ProcessAndSaveRideTelemetryAsync(string groupName)
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
                summary.AverageMovingSpeedKmh = Math.Round(summary.TotalDistanceKm / summary.MovingTime.TotalHours, 1);

            await LocalRideLogger.SaveRideAsync(summary);
            _rideCache.HardResetAll();

            return summary;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving ride: {ex.Message}");
            return null;
        }
    }
    private readonly Dictionary<string, DateTime> _riderLastUpdateTimes = new();

    public RiderRelativeStatus CalculateRiderStatus(string riderId, Location newLoc, Location myLoc, RideStateService rideCache, GroupSettingsDto settings)
    {
        var now = DateTime.UtcNow;
        double speedKmh = 0;

        // 1. Calculate Speed
        if (rideCache.OtherRiderLocations.TryGetValue(riderId, out var oldLoc) && _riderLastUpdateTimes.TryGetValue(riderId, out var lastTime))
        {
            double distKm = Location.CalculateDistance(oldLoc, newLoc, DistanceUnits.Kilometers);
            double hours = (now - lastTime).TotalHours;

            if (hours > 0)
            {
                speedKmh = distKm / hours;
                if (speedKmh > 250) speedKmh = rideCache.OtherRiderSpeeds.GetValueOrDefault(riderId, 0);
            }
        }

        rideCache.OtherRiderLocations[riderId] = newLoc;
        rideCache.OtherRiderSpeeds[riderId] = speedKmh;
        _riderLastUpdateTimes[riderId] = now;

        string speedStr = speedKmh > 1 ? $"{Math.Round(speedKmh)} km/h" : "Stopped";
        string gapStatus = "Nearby";
        Color gapColor = Colors.MediumSeaGreen;

        // 2. Ahead / Behind Math
        if (myLoc != null && rideCache.CurrentRoutePoints != null && rideCache.CurrentRoutePoints.Count > 0)
        {
            double distToThemKm = Location.CalculateDistance(myLoc, newLoc, DistanceUnits.Kilometers);
            double distToThemMeters = distToThemKm * 1000;

            if (distToThemMeters > 50)
            {
                int theirIndex = 0;
                double minDist = double.MaxValue;

                for (int i = 0; i < rideCache.CurrentRoutePoints.Count; i += 5)
                {
                    double d = Location.CalculateDistance(newLoc, rideCache.CurrentRoutePoints[i], DistanceUnits.Kilometers);
                    if (d < minDist) { minDist = d; theirIndex = i; }
                }

                string distDisplay = distToThemMeters > 1000 ? $"{Math.Round(distToThemKm, 1)} km" : $"{Math.Round(distToThemMeters)}m";
                int lagLimit = settings?.MaxLagDistanceMeters ?? 1000;

                if (theirIndex > rideCache.CurrentRouteIndex + 5)
                {
                    gapStatus = $"{distDisplay} Ahead";
                    gapColor = Colors.DodgerBlue;
                }
                else if (theirIndex < rideCache.CurrentRouteIndex - 5)
                {
                    gapStatus = $"{distDisplay} Behind";
                    gapColor = distToThemMeters > lagLimit ? Colors.Red : Colors.Orange;
                }
                else if (theirIndex <= 5 && rideCache.CurrentRouteIndex <= 5 && distToThemMeters > 100)
                {
                    gapStatus = $"{distDisplay} Behind";
                    gapColor = distToThemMeters > lagLimit ? Colors.Red : Colors.Orange;
                }
                else
                {
                    gapStatus = $"{distDisplay} Away";
                    gapColor = Colors.Gray;
                }
            }
        }

        return new RiderRelativeStatus
        {
            SpeedKmh = speedKmh,
            SpeedStr = speedStr,
            StatusStr = gapStatus,
            StatusColor = gapColor,
            InterpolatedLocation = newLoc
        };
    }
}