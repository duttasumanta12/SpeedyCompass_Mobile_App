using Microsoft.Extensions.Configuration;
using Microsoft.Maui.Controls.Maps;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Constants;
using System.Globalization;
using System.Text.Json;

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
}

public interface IRoutingEngine
{
    List<Location> DecodeGooglePolyline(string encodedPoints);
    string EncodeLocationList(List<Location> points);
    Task<RouteUIData> FetchAndBuildPolylineAsync(Location origin, Location dest, Location meetup, Color routeColor, bool includeVoiceSteps, bool isReroute);
    Task<Location> CalculateDynamicMeetupPointAsync();
    Task<RouteTelemetryResult> ProcessRouteTelemetryAsync(Location currentLocation, RideStateService rideCache, RouteDeviationEngine deviationEngine, List<RouteStep> activeRouteSteps, bool currentHasAnnouncedArrival, Location currentLastAnnouncedTurn, bool voiceNavEnabled, CancellationToken cancellationToken);
    Task<RouteCalculationResult> GetRouteDataAsync(Location origin, Location dest, Location meetup = null, bool includeVoiceSteps = false, bool isReroute = false);
    Location GetLocationAheadOnRoute(List<Location> routePoints, int currentIndex, double targetDistanceKm);
}

public class RoutingEngine : IRoutingEngine
{
    private readonly HttpClient _httpClient;
    private readonly string _googleApiKey;
    private readonly RideStateService _rideCache;

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

            string fieldMask = "routes.polyline.encodedPolyline,routes.distanceMeters,routes.duration";
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

    public async Task<RouteUIData> FetchAndBuildPolylineAsync(Location origin, Location dest, Location meetup, Color routeColor, bool includeVoiceSteps, bool isReroute = false)
    {
        var routeData = await GetRouteDataAsync(origin, dest, meetup, includeVoiceSteps, isReroute);
        if (string.IsNullOrEmpty(routeData?.EncodedPolyline) || routeData.DecodedPoints.Count == 0) return null;

        var combinedPoints = new List<Location>();
        int seamIndex = 0;

        if (isReroute && _rideCache.CurrentRoutePoints != null)
        {
            var historySlice = _rideCache.CurrentRoutePoints.Take(_rideCache.CurrentRouteIndex).ToList();
            combinedPoints.AddRange(historySlice);
            seamIndex = historySlice.Count;
        }

        combinedPoints.AddRange(routeData.DecodedPoints);

        var polyline = new Polyline { StrokeColor = routeColor, StrokeWidth = 22f };
        foreach (var coord in combinedPoints) polyline.Geopath.Add(coord);

        var mapBubbles = new List<MapBubble>();
        var turnOverlays = new List<MapElement>(); // <-- NEW

        if (routeData.VoiceSteps != null)
        {
            foreach (var step in routeData.VoiceSteps)
            {
                var instruction = step.Instruction.Split(Environment.NewLine)[0] ?? string.Empty;
                var dirData = GetDirectionData(instruction);

                // =====================================================================
                // THE FIX: If it's a Ramp or Exit, create a floating text bubble!
                // =====================================================================
                if (instruction.Contains("ramp") || instruction.Contains("fork") ||
                    instruction.Contains("merge") || instruction.Contains("flyover") || instruction.Contains("overpass"))
                {
                    var bubbleIcons = new List<string>();

                    // 1. Primary Infrastructure Icon
                    if (instruction.Contains("flyover") || instruction.Contains("overpass"))
                        bubbleIcons.Add("flyover"); // Use the Material symbol for a bridge!
                    else if (instruction.Contains("merge"))
                    {
                        bubbleIcons.Add("merge");
                        instruction = "Merging Roads";
                    }
                    else if (instruction.Contains("fork"))
                        bubbleIcons.Add(instruction.Contains("left") ? "fork_left" : "fork_right");
                    else if (instruction.Contains("ramp") || instruction.Contains("exit"))
                        bubbleIcons.Add(instruction.Contains("left") ? "ramp_left" : "ramp_right");

                    // 2. Secondary Direction Icon (For complex maneuvers like Flyovers)
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
                        IconNames = bubbleIcons, // Pass the list!
                        Instruction = instruction
                    });
                }

                if (dirData.IconName != "straight")
                {
                    double roadHeading = 0;
                    Location pinPlacement = step.TurnLocation;


                    // 1. Find the exact array index of the intersection
                    int turnIdx = 0;
                    double minDist = double.MaxValue;
                    for (int i = 0; i < combinedPoints.Count; i++)
                    {
                        double d = Location.CalculateDistance(step.TurnLocation, combinedPoints[i], DistanceUnits.Kilometers);
                        if (d < minDist) { minDist = d; turnIdx = i; }
                    }

                    // 2. Trace backwards ~25 meters
                    double backDist = 0;
                    int startIdx = turnIdx;
                    while (startIdx > 0 && backDist < 0.020) // 0.025 km = 25m
                    {
                        backDist += Location.CalculateDistance(combinedPoints[startIdx], combinedPoints[startIdx - 1], DistanceUnits.Kilometers);
                        startIdx--;
                    }

                    // 3. Trace forwards ~25 meters
                    double fwdDist = 0;
                    int endIdx = turnIdx;
                    while (endIdx < combinedPoints.Count - 1 && fwdDist < 0.020)
                    {
                        fwdDist += Location.CalculateDistance(combinedPoints[endIdx], combinedPoints[endIdx + 1], DistanceUnits.Kilometers);
                        endIdx++;
                    }

                    // 4. Extract the curved path segment
                    var overlayCoords = new List<Location>();
                    for (int i = startIdx; i <= endIdx; i++) overlayCoords.Add(combinedPoints[i]);

                    if (overlayCoords.Count >= 2)
                    {
                        var whiteLine = new Polyline { StrokeColor = Colors.White, StrokeWidth = 10f };
                        foreach (var c in overlayCoords) whiteLine.Geopath.Add(c);
                        turnOverlays.Add(whiteLine);

                        // =====================================================================
                        // THE FIX: NATIVE SCALING POLYGON ARROWHEAD
                        // =====================================================================
                        var arrowTip = overlayCoords.Last();
                        var arrowBase = overlayCoords[overlayCoords.Count - 2];
                        double arrowBearing = CalculateBearing(arrowBase, arrowTip);

                        // Create a physical shape mapped to the globe (10 meters long)
                        double arrowSizeKm = 0.010;
                        var arrowPolygon = CreateArrowhead(arrowTip, arrowBearing, arrowSizeKm);
                        turnOverlays.Add(arrowPolygon);

                        // Nullify pin placement so we DO NOT draw the Android marker!
                        pinPlacement = null;
                    }
                    else if (turnIdx < combinedPoints.Count - 1)
                    {
                        // Fallback if the route ends immediately at the turn
                        roadHeading = CalculateBearing(combinedPoints[turnIdx], combinedPoints[turnIdx + 1]);
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
            TurnOverlays = turnOverlays ,
            MapBubbles = mapBubbles
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
                DistLeftKm = distLeft
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
}