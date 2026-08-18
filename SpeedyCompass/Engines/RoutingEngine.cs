using Microsoft.Extensions.Configuration;
using Microsoft.Maui.Controls.Maps;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Constants;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeedyCompass.Engines;

public class MapBubble
{
    public Location Location { get; set; }
    public List<string> IconNames { get; set; } = new();
    public string Instruction { get; set; }
}

// A clean wrapper to pass data back to the UI
public class RouteCalculationResult
{
    public string EncodedPolyline { get; set; }
    public List<Location> DecodedPoints { get; set; } = new();
    public List<RouteStep> VoiceSteps { get; set; } = new();
    public double DistanceKm { get; set; }
    public string EtaText { get; set; } = "0m";
    public List<SpeedInterval> TrafficData { get; set; } = new();
}
public class RouteUIData
{
    public string EncodedPolyline { get; set; }
    public Polyline MapLine { get; set; }
    public string DistanceKm { get; set; }
    public string EtaText { get; set; }
    public List<Location> DecodedPoints { get; set; }
    public List<RouteStep> VoiceSteps { get; set; }
    public int SpliceIndex { get; internal set; }
    public List<MapElement> TurnOverlays { get; set; } = new();
    public List<MapBubble> MapBubbles { get; set; } = new();
    public List<SpeedInterval> TrafficData { get; set; } = new();
}
public class SpeedInterval
{
    [JsonPropertyName("startPolylinePointIndex")]
    public int StartPolylinePointIndex { get; set; }

    [JsonPropertyName("endPolylinePointIndex")]
    public int EndPolylinePointIndex { get; set; }

    [JsonPropertyName("speed")]
    public string Speed { get; set; } // Returns "NORMAL", "SLOW", or "TRAFFIC_JAM"
}
// 2) Add inside RoutingEngine class (near other private fields)
public sealed class TrafficWindowFetchResult
{
    public List<Location> DecodedPoints { get; set; } = new();
    public List<SpeedInterval> Intervals { get; set; } = new();
}

// 2. DTO for the Telemetry UI updates
public class RouteTelemetryResult
{
    public bool IsOffRoute { get; set; }
    public bool ShouldReroute { get; set; }
    public string UserMessage { get; set; }
    public Color AlertColor { get; set; }

    public int NewRouteIndex { get; set; }
    public string DistLeftStr { get; set; }
    public string TotalTravelStr { get; set; }
    public string TotalRouteStr { get; set; }
    public double ProgressVal { get; set; }
    public string ProgressPercentStr { get; set; }
    public string EtaStr { get; set; }

    public bool ShowNextTurn { get; set; }
    public string NextTurnDistStr { get; set; }
    public string NextTurnInstr { get; set; }
    public string NextTurnIcon { get; set; }

    // Voice & Tripwires
    public bool SpeakDestinationReached { get; set; }
    public bool SpeakNextTurn { get; set; }
    public string VoiceInstructionToSpeak { get; set; }
    public bool UpdatedHasAnnouncedArrival { get; set; }
    public Location UpdatedLastAnnouncedTurn { get; set; }
    public double DistLeftKm { get; set; } // <-- Add this!
    public string TrafficAlertMessage { get; set; }
    public bool SpeakTrafficAlert { get; set; }
}

public interface IRoutingEngine
{
    List<Location> DecodeGooglePolyline(string encodedPoints);
    string EncodeLocationList(List<Location> points);
    RouteUIData BuildRouteVisuals(RouteCalculationResult routeData, Color routeColor, bool generateOverlays, bool isReroute = false);
    Task<Location> CalculateDynamicMeetupPointAsync();
    Task<RouteTelemetryResult> ProcessRouteTelemetryAsync(Location currentLocation, RideStateService rideCache, RouteDeviationEngine deviationEngine, List<RouteStep> activeRouteSteps, bool currentHasAnnouncedArrival, Location currentLastAnnouncedTurn, bool voiceNavEnabled, CancellationToken cancellationToken);
    Task<RouteCalculationResult> GetRouteDataAsync(Location origin, Location dest, Location meetup = null, bool includeVoiceSteps = false, bool isReroute = false);
    Location GetLocationAheadOnRoute(List<Location> routePoints, int currentIndex, double targetDistanceKm);
    Task RefreshTrafficWindowIfNeededAsync(Location currentLocation, double currentSpeedKmh, CancellationToken cancellationToken = default);
    Location SnapToRouteLine(Location rawLocation, List<Location> routePoints, int currentIndex);
    Location CalculateCenterOfMassMeetup(List<Location> riderLocations);
    Polyline CreateStraightLineSpiderweb(Location lostRider, Location target, Color riderColor);
    Task<RouteCalculationResult> GetMapboxOverviewRouteAsync(List<Location> routePoints);
}

public class RoutingEngine : IRoutingEngine
{
    private readonly HttpClient _httpClient;
    private readonly string _googleApiKey;
    private readonly RideStateService _rideCache;
    private readonly TrafficAwarenessEngine _trafficEngine = new();

    public RoutingEngine(IConfiguration configuration, RideStateService rideCache)
    {
        _httpClient = new HttpClient();
        _googleApiKey = configuration["GoogleApiKey"] ?? "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk";
        _rideCache = rideCache;
    }

    // 🔄 REPLACE entire method
    // Note the added 'bool isReroute = false' parameter at the end!
    public async Task<RouteCalculationResult> GetRouteDataAsync(Location origin, Location dest, Location meetup = null, bool includeVoiceSteps = false, bool isReroute = false)
    {
        var result = new RouteCalculationResult();
        try
        {
            // =====================================================================
            // THE FIX: Safely extract the MAUI compass course for Google
            // =====================================================================
            int? validHeading = null;
            if (isReroute && origin.Course.HasValue && !double.IsNaN(origin.Course.Value))
            {
                validHeading = (int)Math.Round(origin.Course.Value) % 360;
                if (validHeading < 0) validHeading += 360;
            }

            var requestBody = new RoutesRequest
            {
                Origin = new RouteWaypoint
                {
                    Location = new RouteLocation
                    {
                        LatLng = new RouteLatLng { Latitude = origin.Latitude, Longitude = origin.Longitude },
                        Heading = validHeading // Injects the heading (or stays null and is ignored by JSON)
                    }
                },
                Destination = new RouteWaypoint
                {
                    Location = new RouteLocation
                    {
                        LatLng = new RouteLatLng { Latitude = dest.Latitude, Longitude = dest.Longitude }
                    }
                }
            };

            if (meetup != null)
            {
                requestBody.Intermediates = new List<RouteWaypoint> {
                new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = meetup.Latitude, Longitude = meetup.Longitude } } }
            };
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

            string fieldMask = "routes.polyline.encodedPolyline,routes.distanceMeters,routes.duration,routes.travelAdvisory.speedReadingIntervals";
            if (includeVoiceSteps)
                fieldMask += ",routes.legs.steps.startLocation,routes.legs.steps.navigationInstruction";

            request.Headers.Add("X-Goog-FieldMask", fieldMask);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var routeResult = JsonSerializer.Deserialize<RoutesResponse>(await response.Content.ReadAsStringAsync());
                var mainRoute = routeResult?.Routes?.FirstOrDefault();

                if (mainRoute != null)
                {
                    result.EncodedPolyline = mainRoute.Polyline.EncodedPolyline;
                    result.DecodedPoints = DecodeGooglePolyline(result.EncodedPolyline);
                    result.DistanceKm = Math.Round(mainRoute.DistanceMeters / 1000.0, 1);
                    result.EtaText = ToEtaText(mainRoute.Duration);

                    if (mainRoute.TravelAdvisory?.SpeedReadingIntervals != null)
                    {
                        result.TrafficData = mainRoute.TravelAdvisory.SpeedReadingIntervals
                            .Select(x => new SpeedInterval
                            {
                                StartPolylinePointIndex = x.StartPolylinePointIndex,
                                EndPolylinePointIndex = x.EndPolylinePointIndex,
                                Speed = x.Speed ?? "NORMAL"
                            })
                            .ToList();
                    }
                    else
                    {
                        result.TrafficData = new List<SpeedInterval>();
                    }

                    if (includeVoiceSteps && mainRoute.Legs != null)
                    {
                        foreach (var leg in mainRoute.Legs)
                        {
                            if (leg.Steps == null) continue;
                            foreach (var step in leg.Steps)
                            {
                                if (step.NavigationInstruction != null && !string.IsNullOrEmpty(step.NavigationInstruction.Instructions) && step.StartLocation?.LatLng != null)
                                {
                                    result.VoiceSteps.Add(new RouteStep
                                    {
                                        TurnLocation = new Location(step.StartLocation.LatLng.Latitude, step.StartLocation.LatLng.Longitude),
                                        Instruction = step.NavigationInstruction.Instructions
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Routing Error: {ex.Message}"); }
        return result;
    }

    private static string ToEtaText(string? durationRaw)
    {
        if (string.IsNullOrWhiteSpace(durationRaw)) return "0m";

        var cleaned = durationRaw.Replace("s", "", StringComparison.OrdinalIgnoreCase);
        if (!double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            return "0m";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts.Hours}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }

    public List<Location> DecodeGooglePolyline(string encodedPoints)
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

    public string EncodeLocationList(List<Location> points)
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
    // =====================================================================
    // 1. ROUTE POLYLINE ORCHESTRATOR
    // =====================================================================
    private (string IconName, bool IsFlatArrow) GetDirectionData(string instruction)
    {
        var direction = GetTurnDirection(instruction);
        return direction switch
        {
            TurnDirectionEnum.UTurnLeft => ("u_turn_left", false),
            TurnDirectionEnum.UTurnRight => ("u_turn_right", false),
            TurnDirectionEnum.RoundaboutLeft => ("roundabout_left", false),
            TurnDirectionEnum.RoundaboutRight => ("roundabout_right", false),
            TurnDirectionEnum.RampLeft => ("ramp_left", false),
            TurnDirectionEnum.RampRight => ("ramp_right", false),
            TurnDirectionEnum.ForkLeft => ("fork_left", false),
            TurnDirectionEnum.ForkRight => ("fork_right", false),
            TurnDirectionEnum.Merge => ("merge", false),
            TurnDirectionEnum.Destination => ("location_on", false),

            TurnDirectionEnum.SharpLeft => ("turn_sharp_left", true),
            TurnDirectionEnum.SharpRight => ("turn_sharp_right", true),
            TurnDirectionEnum.SlightLeft => ("turn_slight_left", true),
            TurnDirectionEnum.SlightRight => ("turn_slight_right", true),
            TurnDirectionEnum.KeepLeft => ("turn_slight_left", true),
            TurnDirectionEnum.KeepRight => ("turn_slight_right", true),
            TurnDirectionEnum.TurnLeft => ("turn_left", true),
            TurnDirectionEnum.TurnRight => ("turn_right", true),

            _ => ("straight", false)
        };
    }
    private static TurnDirectionEnum GetTurnDirection(string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return TurnDirectionEnum.Straight;

        var text = instruction.Trim().ToLowerInvariant();

        bool hasLeft = System.Text.RegularExpressions.Regex.IsMatch(text, @"\bleft\b");
        bool hasRight = System.Text.RegularExpressions.Regex.IsMatch(text, @"\bright\b");

        if (text.Contains("u-turn"))
            return hasRight ? TurnDirectionEnum.UTurnRight : TurnDirectionEnum.UTurnLeft;

        if (text.Contains("roundabout") || text.Contains("rotary"))
            return hasRight ? TurnDirectionEnum.RoundaboutRight : TurnDirectionEnum.RoundaboutLeft;

        if (text.Contains("exit") || text.Contains("ramp"))
            return hasLeft ? TurnDirectionEnum.RampLeft : TurnDirectionEnum.RampRight;

        if (text.Contains("merge"))
            return TurnDirectionEnum.Merge;

        if (text.Contains("fork"))
            return hasLeft ? TurnDirectionEnum.ForkLeft : TurnDirectionEnum.ForkRight;

        if (text.Contains("sharp left")) return TurnDirectionEnum.SharpLeft;
        if (text.Contains("sharp right")) return TurnDirectionEnum.SharpRight;

        if (text.Contains("slight left")) return TurnDirectionEnum.SlightLeft;
        if (text.Contains("slight right")) return TurnDirectionEnum.SlightRight;

        if (text.Contains("keep left")) return TurnDirectionEnum.KeepLeft;
        if (text.Contains("keep right")) return TurnDirectionEnum.KeepRight;

        if (text.Contains("flyover") || text.Contains("overpass"))
        {
            if (hasLeft) return TurnDirectionEnum.SlightLeft;
            if (hasRight) return TurnDirectionEnum.SlightRight;
            return TurnDirectionEnum.Straight;
        }

        if (text.Contains("arrive") || text.Contains("destination"))
            return TurnDirectionEnum.Destination;

        if (text.Contains("turn left") || (hasLeft && !hasRight))
            return TurnDirectionEnum.TurnLeft;

        if (text.Contains("turn right") || (hasRight && !hasLeft))
            return TurnDirectionEnum.TurnRight;

        return TurnDirectionEnum.Straight;
    }

    // =====================================================================
    // THE FIX: PURE GRAPHICS BUILDER (DECOUPLED FROM API FETCH)
    // =====================================================================
    public RouteUIData BuildRouteVisuals(RouteCalculationResult routeData, Color routeColor, bool generateOverlays, bool isReroute = false)
    {
        if (routeData == null || routeData.DecodedPoints.Count == 0) return null;

        var combinedPoints = new List<Location>();
        int seamIndex = 0;

        // 1. Reroute Splice Logic
        if (isReroute && _rideCache.CurrentRoutePoints != null)
        {
            var historySlice = _rideCache.CurrentRoutePoints.Take(_rideCache.CurrentRouteIndex).ToList();
            combinedPoints.AddRange(historySlice);
            seamIndex = historySlice.Count;
        }
        combinedPoints.AddRange(routeData.DecodedPoints);

        // 2. Base Route Line
        var polyline = new Polyline { StrokeColor = routeColor, StrokeWidth = 22f };
        foreach (var coord in combinedPoints) polyline.Geopath.Add(coord);

        var mapBubbles = new List<MapBubble>();
        var turnOverlays = new List<MapElement>();

        // 3. ON-DEMAND OVERLAYS (Only runs when Navigation is active!)
        if (generateOverlays && routeData.VoiceSteps != null)
        {
            foreach (var step in routeData.VoiceSteps)
            {
                var instruction = step.Instruction.Split(Environment.NewLine)[0] ?? string.Empty;
                var dirData = GetDirectionData(instruction);

                // --- BUBBLE GENERATOR ---
                if (instruction.Contains("ramp") || instruction.Contains("fork") ||
                    instruction.Contains("merge") || instruction.Contains("flyover") || instruction.Contains("overpass"))
                {
                    var bubbleIcons = new List<string>();

                    if (instruction.Contains("flyover") || instruction.Contains("overpass"))
                        bubbleIcons.Add("flyover");
                    else if (instruction.Contains("merge"))
                    {
                        bubbleIcons.Add("merge");
                        instruction = "Merging Roads";
                    }
                    else if (instruction.Contains("fork"))
                        bubbleIcons.Add(instruction.Contains("left") ? "fork_left" : "fork_right");
                    else if (instruction.Contains("ramp") || instruction.Contains("exit"))
                        bubbleIcons.Add(instruction.Contains("left") ? "ramp_left" : "ramp_right");

                    if (instruction.Contains("flyover") || instruction.Contains("overpass"))
                    {
                        if (instruction.Contains("left")) bubbleIcons.Add("turn_slight_left");
                        else if (instruction.Contains("right")) bubbleIcons.Add("turn_slight_right");
                        else bubbleIcons.Add("straight");
                        instruction = "Take flyover";
                    }

                    mapBubbles.Add(new MapBubble
                    {
                        Location = step.TurnLocation,
                        IconNames = bubbleIcons,
                        Instruction = instruction
                    });
                }

                // --- WHITE TURN TRACK & ARROW GENERATOR ---
                if (dirData.IconName != "straight")
                {
                    double roadHeading = 0;
                    Location pinPlacement = step.TurnLocation;

                    int turnIdx = 0;
                    double minDist = double.MaxValue;
                    for (int i = 0; i < combinedPoints.Count; i++)
                    {
                        double d = Location.CalculateDistance(step.TurnLocation, combinedPoints[i], DistanceUnits.Kilometers);
                        if (d < minDist) { minDist = d; turnIdx = i; }
                    }

                    double backDist = 0;
                    int startIdx = turnIdx;
                    while (startIdx > 0 && backDist < 0.020)
                    {
                        backDist += Location.CalculateDistance(combinedPoints[startIdx], combinedPoints[startIdx - 1], DistanceUnits.Kilometers);
                        startIdx--;
                    }

                    double fwdDist = 0;
                    int endIdx = turnIdx;
                    while (endIdx < combinedPoints.Count - 1 && fwdDist < 0.020)
                    {
                        fwdDist += Location.CalculateDistance(combinedPoints[endIdx], combinedPoints[endIdx + 1], DistanceUnits.Kilometers);
                        endIdx++;
                    }

                    var overlayCoords = new List<Location>();
                    for (int i = startIdx; i <= endIdx; i++) overlayCoords.Add(combinedPoints[i]);

                    if (overlayCoords.Count >= 2)
                    {
                        var whiteLine = new Polyline { StrokeColor = Colors.White, StrokeWidth = 10f };
                        foreach (var c in overlayCoords) whiteLine.Geopath.Add(c);
                        turnOverlays.Add(whiteLine);

                        var arrowTip = overlayCoords.Last();
                        var arrowBase = overlayCoords[overlayCoords.Count - 2];
                        double arrowBearing = CalculateBearing(arrowBase, arrowTip);

                        double arrowSizeKm = 0.010;
                        var arrowPolygon = CreateArrowhead(arrowTip, arrowBearing, arrowSizeKm);
                        turnOverlays.Add(arrowPolygon);

                        pinPlacement = null;
                    }
                }
            }
        }

        return new RouteUIData
        {
            EncodedPolyline = routeData.EncodedPolyline,
            MapLine = polyline,
            DistanceKm = routeData.DistanceKm.ToString(),
            EtaText = routeData.EtaText,
            DecodedPoints = combinedPoints,
            VoiceSteps = routeData.VoiceSteps ?? new List<RouteStep>(),
            SpliceIndex = seamIndex,
            TurnOverlays = turnOverlays,
            MapBubbles = mapBubbles,
            TrafficData = routeData.TrafficData ?? new List<SpeedInterval>()
        };
    }

    // =====================================================================
    // 2. TELEMETRY MATH ORCHESTRATOR
    // =====================================================================
    public async Task<RouteTelemetryResult> ProcessRouteTelemetryAsync(
     Location currentLocation,
     RideStateService rideCache,
     RouteDeviationEngine deviationEngine,
     List<RouteStep> activeRouteSteps,
     bool currentHasAnnouncedArrival,
     Location currentLastAnnouncedTurn,
     bool voiceNavEnabled,
     CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRouteSnapshot = rideCache.CurrentRoutePoints.ToList();

            // --- Odometer Math ---
            double currentSpeedKmh = (currentLocation?.Speed ?? 0) * 3.6;

            if (rideCache.LastOdometerLocation != null)
            {
                double stepDistance = Location.CalculateDistance(rideCache.LastOdometerLocation, currentLocation, DistanceUnits.Kilometers);
                if (stepDistance > 0.01 && stepDistance < 20) rideCache.CumulativeDistanceKm += stepDistance;
            }

            if (currentLocation?.Speed != null)
            {
                double spdKmh = (currentLocation.Speed.Value) * 3.6;
                if (spdKmh > rideCache.MaxSpeedKmh) rideCache.MaxSpeedKmh = spdKmh;

                if (spdKmh < 2) { if (rideCache.LastStopTime == null) rideCache.LastStopTime = DateTime.Now; }
                else if (rideCache.LastStopTime != null)
                {
                    rideCache.TotalStoppedTime += (DateTime.Now - rideCache.LastStopTime.Value);
                    rideCache.LastStopTime = null;
                }
            }

            // --- Closest Point Search ---
            int startIndex = Math.Max(0, rideCache.CurrentRouteIndex - 5);
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

            // --- Distance Left ---
            double distLeft = 0;
            if (currentRouteSnapshot.Count > 1 && closestActualIndex < currentRouteSnapshot.Count)
            {
                distLeft += Location.CalculateDistance(currentLocation, currentRouteSnapshot[closestActualIndex], DistanceUnits.Kilometers);
                for (int j = closestActualIndex; j < currentRouteSnapshot.Count - 1; j++)
                {
                    distLeft += Location.CalculateDistance(currentRouteSnapshot[j], currentRouteSnapshot[j + 1], DistanceUnits.Kilometers);
                }
            }
            else distLeft = Location.CalculateDistance(currentLocation, rideCache.ActiveDestination, DistanceUnits.Kilometers);

            double movingAvg = Math.Max(currentSpeedKmh, 40);
            DateTime eta = DateTime.Now.AddHours(distLeft / movingAvg);

            // --- Traffic Analysis ---
            string trafficAlert = ScanForTrafficAhead(
                closestActualIndex,
                currentRouteSnapshot,
                rideCache.CurrentTrafficData,
                currentSpeedKmh);

            bool speakTraffic = false;
            if (!string.IsNullOrWhiteSpace(trafficAlert))
            {
                speakTraffic = voiceNavEnabled;
                rideCache.LastTrafficAlertTime = DateTime.Now;
            }

            // --- Deviation Analysis ---
            var deviationAnalysis = deviationEngine.AnalyzeRouteDeviation(
                currentLocation, currentRouteSnapshot, closestActualIndex, currentLocation?.Course ?? 0, currentSpeedKmh, rideCache.OffRouteStrikeCount);

            rideCache.OffRouteStrikeCount = deviationEngine.UpdateDeviationStrikes(deviationAnalysis, rideCache.OffRouteStrikeCount);

            int strikeThreshold = deviationEngine.GetRerouteStrikeThreshold(deviationAnalysis);
            bool officiallyLost = rideCache.OffRouteStrikeCount >= strikeThreshold;
            bool shouldReroute = deviationEngine.ShouldPerformReroute(deviationAnalysis, officiallyLost, rideCache.LastRerouteTime);

            // --- Build Result Object ---
            var result = new RouteTelemetryResult
            {
                IsOffRoute = officiallyLost,
                ShouldReroute = shouldReroute,
                UserMessage = deviationAnalysis.UserMessage,
                AlertColor = deviationAnalysis.AlertColor,
                NewRouteIndex = closestActualIndex,
                DistLeftStr = $"{Math.Round(distLeft, 1)} km",
                TotalTravelStr = $"{Math.Round(rideCache.CumulativeDistanceKm, 1)}",
                TotalRouteStr = $"{Math.Round(rideCache.CumulativeDistanceKm + distLeft, 1)} km",
                ProgressVal = distLeft == 0 ? 1.0 : rideCache.CumulativeDistanceKm / (rideCache.CumulativeDistanceKm + distLeft),
                EtaStr = eta.ToString("h:mm tt"),
                UpdatedHasAnnouncedArrival = currentHasAnnouncedArrival,
                UpdatedLastAnnouncedTurn = currentLastAnnouncedTurn,
                DistLeftKm = distLeft,
                TrafficAlertMessage = trafficAlert,
                SpeakTrafficAlert = speakTraffic
             };
            result.ProgressPercentStr = $"{(int)(result.ProgressVal * 100)}%";

            // --- Arrival Prompt ---
            if (distLeft < 0.05 && !currentHasAnnouncedArrival)
            {
                result.UpdatedHasAnnouncedArrival = true;
                result.SpeakDestinationReached = voiceNavEnabled;
            }

            // --- Next Turn Math ---
            if (activeRouteSteps.Count > 0)
            {
                var nextStep = activeRouteSteps[0];
                double distToTurnMeters = Location.CalculateDistance(currentLocation, nextStep.TurnLocation, DistanceUnits.Kilometers) * 1000;
                string instruction = nextStep.Instruction;

                if (distToTurnMeters <= 400 && currentLastAnnouncedTurn != nextStep.TurnLocation)
                {
                    result.UpdatedLastAnnouncedTurn = nextStep.TurnLocation;
                    result.SpeakNextTurn = voiceNavEnabled;
                    result.VoiceInstructionToSpeak = instruction;
                }

                if (distToTurnMeters < 30)
                {
                    activeRouteSteps.RemoveAt(0);
                    if (activeRouteSteps.Count > 0)
                    {
                        nextStep = activeRouteSteps[0];
                        distToTurnMeters = Location.CalculateDistance(currentLocation, nextStep.TurnLocation, DistanceUnits.Kilometers) * 1000;
                        instruction = nextStep.Instruction;
                    }
                }

                if (activeRouteSteps.Count > 0)
                {
                    result.ShowNextTurn = true;
                    result.NextTurnDistStr = distToTurnMeters > 1000 ? $"{Math.Round(distToTurnMeters / 1000.0, 1)} km" : $"{Math.Round(distToTurnMeters)}m";
                    result.NextTurnInstr = instruction;

                    // =====================================================================
                    // THE FIX: Reuse our existing Material Symbols helper method!
                    // This replaces ~30 lines of if/else logic with a single clean call.
                    // =====================================================================
                    var dirData = GetDirectionData(instruction);
                    result.NextTurnIcon = dirData.IconName;
                }
            }

            return result;
        }, cancellationToken);
    }
    public async Task<Location> CalculateDynamicMeetupPointAsync()
    {
        // Pull everything directly from the injected cache
        var currentRoute = _rideCache.CurrentRoutePoints;
        int currentRouteIndex = _rideCache.CurrentRouteIndex;
        var riderLocations = _rideCache.OtherRiderLocations.Values.ToList();
        var destination = _rideCache.ActiveDestination;

        if (currentRoute == null || currentRoute.Count == 0 || riderLocations.Count == 0 || destination == null)
            return null;

        var lostRiders = new List<Location>();

        // 1. FAST FILTER
        foreach (var loc in riderLocations)
        {
            bool isOnPath = false;
            for (int i = Math.Max(0, currentRouteIndex - 5); i < currentRoute.Count; i += 3)
            {
                if (Location.CalculateDistance(loc, currentRoute[i], DistanceUnits.Kilometers) < 0.15)
                {
                    isOnPath = true;
                    break;
                }
            }
            if (!isOnPath) lostRiders.Add(loc);
        }

        // 2. AUTO-CLEAR
        if (lostRiders.Count == 0) return null;

        int furthestConvergenceIndex = Math.Max(0, currentRouteIndex);

        // 3. GOOGLE QUERY
        var routeTasks = lostRiders.Select(loc => GetRouteDataAsync(loc, destination)).ToList();
        var strayRoutes = await Task.WhenAll(routeTasks);

        // 4. FORWARD MERGE
        foreach (var strayRoute in strayRoutes)
        {
            if (strayRoute?.DecodedPoints == null || strayRoute.DecodedPoints.Count == 0) continue;

            int mergeIndex = -1;

            for (int i = Math.Max(0, currentRouteIndex); i < currentRoute.Count; i += 2)
            {
                var leadPt = currentRoute[i];
                bool doesMergeHere = strayRoute.DecodedPoints.Any(strayPt =>
                    Location.CalculateDistance(leadPt, strayPt, DistanceUnits.Kilometers) < 0.15);

                if (doesMergeHere)
                {
                    mergeIndex = i;
                    break;
                }
            }

            if (mergeIndex > furthestConvergenceIndex)
            {
                furthestConvergenceIndex = mergeIndex;
            }
        }

        // 5. SAFETY BUFFER
        double accumulatedDist = 0;
        int finalTargetIndex = furthestConvergenceIndex;

        while (finalTargetIndex < currentRoute.Count - 1 && accumulatedDist < 5.0)
        {
            accumulatedDist += Location.CalculateDistance(currentRoute[finalTargetIndex], currentRoute[finalTargetIndex + 1], DistanceUnits.Kilometers);
            finalTargetIndex++;
        }

        return currentRoute[finalTargetIndex];
    }
    // Grabs a coordinate roughly X kilometers ahead of your current position on the route line
    public Location GetLocationAheadOnRoute(List<Location> routePoints, int currentIndex, double targetDistanceKm)
    {
        if (routePoints == null || currentIndex < 0 || currentIndex >= routePoints.Count)
            return null;

        double accumulatedDistance = 0;

        for (int i = currentIndex; i < routePoints.Count - 1; i++)
        {
            double segmentDist = Location.CalculateDistance(routePoints[i], routePoints[i + 1], DistanceUnits.Kilometers);
            accumulatedDistance += segmentDist;

            if (accumulatedDistance >= targetDistanceKm)
            {
                return routePoints[i + 1];
            }
        }

        // If the route is shorter than the look-ahead distance, just check the destination!
        return routePoints.Last();
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
    private double CalculateBearing(Location pt1, Location pt2)
    {
        double lat1 = pt1.Latitude * Math.PI / 180.0;
        double lon1 = pt1.Longitude * Math.PI / 180.0;
        double lat2 = pt2.Latitude * Math.PI / 180.0;
        double lon2 = pt2.Longitude * Math.PI / 180.0;

        double dLon = lon2 - lon1;
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        double brng = Math.Atan2(y, x);
        return (brng * 180.0 / Math.PI + 360) % 360;
    }
    private Polygon CreateArrowhead(Location tip, double bearingDegrees, double sizeKm)
    {
        double bearingRad = bearingDegrees * Math.PI / 180.0;
        double latPerKm = 1.0 / 111.0;
        double lonPerKm = 1.0 / (111.0 * Math.Cos(tip.Latitude * Math.PI / 180.0));

        // Arrow dimensions
        double length = sizeKm * 1.2; // Length from tip to base
        double width = sizeKm * 0.8;  // Width at base

        // Calculate base center point (behind the tip)
        double baseLat = tip.Latitude - (Math.Cos(bearingRad) * length * latPerKm);
        double baseLon = tip.Longitude - (Math.Sin(bearingRad) * length * lonPerKm);

        // Perpendicular angle for wings
        double perpRad = bearingRad + Math.PI / 2.0;
        double wingLatDelta = Math.Cos(perpRad) * width * latPerKm;
        double wingLonDelta = Math.Sin(perpRad) * width * lonPerKm;

        var arrow = new Polygon
        {
            StrokeColor = Colors.White,
            FillColor = Colors.White,
            StrokeWidth = 1f
        };

        // Triangle points: tip, left wing, right wing
        arrow.Geopath.Add(tip); // Tip
        arrow.Geopath.Add(new Location(baseLat + wingLatDelta, baseLon + wingLonDelta)); // Left wing
        arrow.Geopath.Add(new Location(baseLat - wingLatDelta, baseLon - wingLonDelta)); // Right wing

        return arrow;
    }

    public string ScanForTrafficAhead(int currentIndex, List<Location> polylinePoints, List<SpeedInterval> trafficData, double currentSpeedKmh)
    {
        var analysis = _trafficEngine.AnalyzeTrafficAhead(
            currentIndex,
            polylinePoints,
            trafficData,
            currentSpeedKmh,
            _rideCache.LastTrafficAlertTime);

        return analysis.ShouldAlert ? analysis.Message : null;
    }

    public async Task RefreshTrafficWindowIfNeededAsync(Location currentLocation, double currentSpeedKmh, CancellationToken cancellationToken = default)
    {
        if (currentLocation == null) return;
        if (_rideCache.ActiveDestination == null) return;
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count < 2) return;

        var routePoints = _rideCache.CurrentRoutePoints;
        int currentIndex = FindClosestRouteIndex(currentLocation, routePoints, _rideCache.CurrentRouteIndex);

        if (!ShouldRefreshTrafficData(currentSpeedKmh, currentIndex))
            return;

        double horizonKm = GetTrafficHorizonKm(currentSpeedKmh); // up to 20km
        int horizonEndIndex = GetIndexAtDistanceAhead(currentIndex, horizonKm, routePoints);
        if (horizonEndIndex <= currentIndex) return;

        Location horizonDestination = routePoints[horizonEndIndex];

        var trafficWindow = await FetchTrafficWindowAsync(currentLocation, horizonDestination, cancellationToken);
        if (trafficWindow == null || trafficWindow.DecodedPoints.Count == 0 || trafficWindow.Intervals.Count == 0)
        {
            _rideCache.LastTrafficRefreshTime = DateTime.UtcNow;
            _rideCache.LastTrafficRefreshRouteIndex = currentIndex;
            return;
        }

        var mappedIntervals = MapWindowIntervalsToMainRoute(
            trafficWindow.Intervals,
            trafficWindow.DecodedPoints,
            routePoints,
            currentIndex,
            horizonEndIndex);

        _rideCache.CurrentTrafficData = MergeTrafficIntervals(
            _rideCache.CurrentTrafficData,
            mappedIntervals,
            currentIndex,
            horizonEndIndex);

        // NEW: keep memory stable, drop old segments behind rider
        _rideCache.CurrentTrafficData = PruneTrafficIntervalsBehind(_rideCache.CurrentTrafficData, currentIndex, keepBehindPoints: 80);

        _rideCache.LastTrafficRefreshTime = DateTime.UtcNow;
        _rideCache.LastTrafficRefreshRouteIndex = currentIndex;
    }

    private bool ShouldRefreshTrafficData(double speedKmh, int currentIndex)
    {
        if (_rideCache.CurrentTrafficData == null || _rideCache.CurrentTrafficData.Count == 0)
            return true;

        double elapsedSeconds = (DateTime.UtcNow - _rideCache.LastTrafficRefreshTime).TotalSeconds;
        int indexAdvance = Math.Max(0, currentIndex - _rideCache.LastTrafficRefreshRouteIndex);

        double minRefreshSeconds = speedKmh switch
        {
            < 10 => 150,
            < 30 => 90,
            < 60 => 60,
            < 90 => 45,
            _ => 30
        };

        int minAdvancePoints = speedKmh switch
        {
            < 20 => 12,
            < 50 => 24,
            < 90 => 40,
            _ => 55
        };

        bool severeAhead = HasSevereTrafficAhead(currentIndex, _rideCache.CurrentTrafficData);
        if (severeAhead && elapsedSeconds >= 20) return true;

        return elapsedSeconds >= minRefreshSeconds || indexAdvance >= minAdvancePoints;
    }

    private static bool HasSevereTrafficAhead(int currentIndex, List<SpeedInterval> intervals)
    {
        if (intervals == null || intervals.Count == 0) return false;

        return intervals.Any(i =>
            i.EndPolylinePointIndex >= currentIndex &&
            i.StartPolylinePointIndex <= currentIndex + 120 &&
            string.Equals(i.Speed, "TRAFFIC_JAM", StringComparison.OrdinalIgnoreCase));
    }

    private static double GetTrafficHorizonKm(double speedKmh)
    {
        return speedKmh switch
        {
            < 20 => 6.0,
            < 40 => 10.0,
            < 70 => 14.0,
            < 100 => 18.0,
            _ => 20.0
        };
    }

    private static int GetIndexAtDistanceAhead(int startIndex, double distanceKm, List<Location> points)
    {
        double acc = 0;
        for (int i = startIndex; i < points.Count - 1; i++)
        {
            acc += Location.CalculateDistance(points[i], points[i + 1], DistanceUnits.Kilometers);
            if (acc >= distanceKm) return i + 1;
        }
        return points.Count - 1;
    }

    private async Task<TrafficWindowFetchResult> FetchTrafficWindowAsync(Location origin, Location destination, CancellationToken cancellationToken)
    {
        var result = new TrafficWindowFetchResult();

        int? validHeading = null;
        if (origin.Course.HasValue && !double.IsNaN(origin.Course.Value))
        {
            validHeading = (int)Math.Round(origin.Course.Value) % 360;
            if (validHeading < 0) validHeading += 360;
        }

        // Traffic-window specific request: only what is needed
        var requestBody = new
        {
            origin = new
            {
                location = new
                {
                    latLng = new { latitude = origin.Latitude, longitude = origin.Longitude },
                    heading = validHeading
                }
            },
            destination = new
            {
                location = new
                {
                    latLng = new { latitude = destination.Latitude, longitude = destination.Longitude }
                }
            },
            travelMode = "TWO_WHEELER",
            routingPreference = "TRAFFIC_AWARE_OPTIMAL",
            extraComputations = new[] { "TRAFFIC_ON_POLYLINE" },
            languageCode = "en-US"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
        request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
        request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline,routes.travelAdvisory.speedReadingIntervals");
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return result;

        var routeResult = JsonSerializer.Deserialize<RoutesResponse>(await response.Content.ReadAsStringAsync(cancellationToken));
        var route = routeResult?.Routes?.FirstOrDefault();
        if (route?.Polyline?.EncodedPolyline == null) return result;

        result.DecodedPoints = DecodeGooglePolyline(route.Polyline.EncodedPolyline);
        result.Intervals = route.TravelAdvisory?.SpeedReadingIntervals?
            .Select(x => new SpeedInterval
            {
                StartPolylinePointIndex = x.StartPolylinePointIndex,
                EndPolylinePointIndex = x.EndPolylinePointIndex,
                Speed = x.Speed ?? "NORMAL"
            })
            .ToList() ?? new List<SpeedInterval>();

        return result;
    }

    private List<SpeedInterval> MapWindowIntervalsToMainRoute(
        List<SpeedInterval> windowIntervals,
        List<Location> windowPolyline,
        List<Location> mainRoute,
        int searchStartIndex,
        int searchEndIndex)
    {
        var mapped = new List<SpeedInterval>();
        if (windowIntervals == null || windowIntervals.Count == 0 || windowPolyline == null || windowPolyline.Count == 0)
            return mapped;

        int safeStart = Math.Max(0, searchStartIndex - 10);
        int safeEnd = Math.Min(mainRoute.Count - 1, searchEndIndex + 20);

        foreach (var interval in windowIntervals)
        {
            int ws = Math.Clamp(interval.StartPolylinePointIndex, 0, windowPolyline.Count - 1);
            int we = Math.Clamp(interval.EndPolylinePointIndex, ws, windowPolyline.Count - 1);

            int ms = FindClosestRouteIndex(windowPolyline[ws], mainRoute, safeStart, safeEnd);
            int me = FindClosestRouteIndex(windowPolyline[we], mainRoute, ms, safeEnd);

            if (me < ms) (ms, me) = (me, ms);

            mapped.Add(new SpeedInterval
            {
                StartPolylinePointIndex = ms,
                EndPolylinePointIndex = me,
                Speed = interval.Speed ?? "NORMAL"
            });
        }

        return NormalizeIntervals(mapped);
    }

    private static List<SpeedInterval> MergeTrafficIntervals(
        List<SpeedInterval> existing,
        List<SpeedInterval> fresh,
        int replaceStart,
        int replaceEnd)
    {
        var output = new List<SpeedInterval>();

        if (existing != null)
        {
            output.AddRange(existing.Where(i =>
                i.EndPolylinePointIndex < replaceStart ||
                i.StartPolylinePointIndex > replaceEnd));
        }

        if (fresh != null && fresh.Count > 0)
            output.AddRange(fresh);

        return NormalizeIntervals(output);
    }

    private static List<SpeedInterval> NormalizeIntervals(List<SpeedInterval> input)
    {
        if (input == null || input.Count == 0) return new List<SpeedInterval>();

        var sorted = input
        .OrderBy(i => i.StartPolylinePointIndex)
        .ThenBy(i => i.EndPolylinePointIndex)
        .ToList();

        var merged = new List<SpeedInterval> { new SpeedInterval
        {
            StartPolylinePointIndex = sorted[0].StartPolylinePointIndex,
            EndPolylinePointIndex = sorted[0].EndPolylinePointIndex,
            Speed = sorted[0].Speed
        }};

        for (int i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            var cur = sorted[i];

            if (string.Equals(last.Speed, cur.Speed, StringComparison.OrdinalIgnoreCase) &&
                cur.StartPolylinePointIndex <= last.EndPolylinePointIndex + 1)
            {
                last.EndPolylinePointIndex = Math.Max(last.EndPolylinePointIndex, cur.EndPolylinePointIndex);
            }
            else
            {
                merged.Add(new SpeedInterval
                {
                    StartPolylinePointIndex = cur.StartPolylinePointIndex,
                    EndPolylinePointIndex = cur.EndPolylinePointIndex,
                    Speed = cur.Speed
                });
            }
        }

        return merged;
    }

    private int FindClosestRouteIndex(Location target, List<Location> routePoints, int startIndex, int endIndex)
    {
        int s = Math.Max(0, startIndex);
        int e = Math.Min(routePoints.Count - 1, endIndex);

        double minDist = double.MaxValue;
        int best = s;

        for (int i = s; i <= e; i++)
        {
            double d = Location.CalculateDistance(target, routePoints[i], DistanceUnits.Kilometers);
            if (d < minDist)
            {
                minDist = d;
                best = i;
            }
        }

        return best;
    }
    public Location CalculateCenterOfMassMeetup(List<Location> riderLocations)
    {
        if (riderLocations == null || riderLocations.Count == 0) return null;

        double avgLat = riderLocations.Average(l => l.Latitude);
        double avgLng = riderLocations.Average(l => l.Longitude);

        return new Location(avgLat, avgLng);
    }
    public Polyline CreateStraightLineSpiderweb(Location lostRider, Location target, Color riderColor)
    {
        var polyline = new Polyline
        {
            StrokeColor = riderColor.WithAlpha(0.6f), // Make it slightly transparent
            StrokeWidth = 8f
            // In a custom mapper, you'd set StrokePattern to Dotted here!
        };
        polyline.Geopath.Add(lostRider);
        polyline.Geopath.Add(target);

        return polyline;
    }
    private int FindClosestRouteIndex(Location target, List<Location> routePoints, int anchorIndex)
    {
        int s = Math.Max(0, anchorIndex - 25);
        int e = Math.Min(routePoints.Count - 1, anchorIndex + 80);
        return FindClosestRouteIndex(target, routePoints, s, e);
    }
    private static List<SpeedInterval> PruneTrafficIntervalsBehind(List<SpeedInterval> intervals, int currentIndex, int keepBehindPoints)
    {
        if (intervals == null || intervals.Count == 0) return new List<SpeedInterval>();

        int minIndexToKeep = Math.Max(0, currentIndex - keepBehindPoints);
        return intervals
            .Where(i => i.EndPolylinePointIndex >= minIndexToKeep)
            .ToList();
    }
    // =====================================================================
    // THE FIX: MAGNETIC SNAP-TO-ROUTE ALGORITHM
    // =====================================================================
    public Location SnapToRouteLine(Location rawLocation, List<Location> routePoints, int currentIndex)
    {
        if (routePoints == null || routePoints.Count < 2 || currentIndex < 0)
            return rawLocation;

        // We check the line segment BEFORE and AFTER the rider's current index 
        // to find exactly which chunk of asphalt they are riding next to.
        int prevIdx = Math.Max(0, currentIndex - 1);
        int nextIdx = Math.Min(routePoints.Count - 1, currentIndex + 1);

        var snap1 = GetClosestPointOnSegment(rawLocation, routePoints[prevIdx], routePoints[currentIndex]);
        var snap2 = GetClosestPointOnSegment(rawLocation, routePoints[currentIndex], routePoints[nextIdx]);

        double d1 = Location.CalculateDistance(rawLocation, snap1, DistanceUnits.Kilometers);
        double d2 = Location.CalculateDistance(rawLocation, snap2, DistanceUnits.Kilometers);

        var bestSnap = d1 < d2 ? snap1 : snap2;
        double bestDistMeters = Math.Min(d1, d2) * 1000;

        // THE MAGNET: Only snap if the raw GPS is within 25 meters of the blue line!
        // If it's further, the rider took a detour, so let the pin float free!
        if (bestDistMeters < 25.0)
        {
            return bestSnap;
        }

        return rawLocation;
    }

    private Location GetClosestPointOnSegment(Location p, Location a, Location b)
    {
        // 2D Vector Projection mapping the GPS point onto the Polyline vector
        double dx = b.Longitude - a.Longitude;
        double dy = b.Latitude - a.Latitude;

        if (dx == 0 && dy == 0) return a;

        double t = ((p.Longitude - a.Longitude) * dx + (p.Latitude - a.Latitude) * dy) / (dx * dx + dy * dy);

        // Clamp the projection so it doesn't shoot past the start or end of the line segment
        t = Math.Max(0, Math.Min(1, t));

        return new Location(a.Latitude + t * dy, a.Longitude + t * dx);
    }
    public async Task<RouteCalculationResult> GetMapboxOverviewRouteAsync(List<Location> routePoints)
    {
        var result = new RouteCalculationResult();

        // Mapbox requires at least 2 points, and max 25 points.
        if (routePoints == null || routePoints.Count < 2) return result;
        if (routePoints.Count > 25) routePoints = routePoints.Take(25).ToList();

        try
        {
            string mapboxToken = "pk.eyJ1IjoiZHV0dGFzdW1hbnRhMTIiLCJhIjoiY21zeDc4bG5iMGp5NzJ6c2FiamFjaW1oZyJ9.bMogWiCgbR4u8rZBw_cz5w";

            // Format coordinates as: lon1,lat1;lon2,lat2;lon3,lat3
            var coordString = string.Join(";", routePoints.Select(p =>
                $"{p.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{p.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

            string url = $"https://api.mapbox.com/directions/v5/mapbox/driving/{coordString}?geometries=polyline&overview=full&access_token={mapboxToken}";

            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var route = doc.RootElement.GetProperty("routes")[0];

                result.EncodedPolyline = route.GetProperty("geometry").GetString();
                result.DecodedPoints = DecodeGooglePolyline(result.EncodedPolyline);

                result.DistanceKm = Math.Round(route.GetProperty("distance").GetDouble() / 1000.0, 1);

                double durationSec = route.GetProperty("duration").GetDouble();
                result.EtaText = ToEtaText(durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Mapbox Error: {ex.Message}"); }

        return result;
    }
}