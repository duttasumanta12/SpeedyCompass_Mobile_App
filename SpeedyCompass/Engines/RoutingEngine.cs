using Microsoft.Extensions.Configuration;
using Microsoft.Maui.Controls.Maps;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using System.Globalization;
using System.Text.Json;

namespace SpeedyCompass.Engines;

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
}

public interface IRoutingEngine
{
    Task<RouteCalculationResult> GetRouteDataAsync(Location origin, Location dest, Location meetup = null, bool includeVoiceSteps = false);
    List<Location> DecodeGooglePolyline(string encodedPoints);
    string EncodeLocationList(List<Location> points);
    Task<RouteUIData> FetchAndBuildPolylineAsync(Location origin, Location dest, Location meetup, Color routeColor, bool includeVoiceSteps);
    Location CalculateDynamicMeetupPoint(List<Location> currentRoute, List<Location> riderLocations, int currentRouteIndex);
    Task<RouteTelemetryResult> ProcessRouteTelemetryAsync(Location currentLocation, RideStateService rideCache, RouteDeviationEngine deviationEngine, List<RouteStep> activeRouteSteps, bool currentHasAnnouncedArrival, Location currentLastAnnouncedTurn, bool voiceNavEnabled, CancellationToken cancellationToken);
}

public class RoutingEngine : IRoutingEngine
{
    private readonly HttpClient _httpClient;
    private readonly string _googleApiKey;

    public RoutingEngine(IConfiguration configuration)
    {
        _httpClient = new HttpClient();
        _googleApiKey = configuration["GoogleApiKey"] ?? "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk";
    }

    public async Task<RouteCalculationResult> GetRouteDataAsync(Location origin, Location dest, Location meetup = null, bool includeVoiceSteps = false)
    {
        var result = new RouteCalculationResult();
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
    public async Task<RouteUIData> FetchAndBuildPolylineAsync(Location origin, Location dest, Location meetup, Color routeColor, bool includeVoiceSteps)
    {
        var routeData = await GetRouteDataAsync(origin, dest, meetup, includeVoiceSteps);
        if (string.IsNullOrEmpty(routeData?.EncodedPolyline) || routeData.DecodedPoints.Count == 0) return null;

        // Build the physical map line in the engine!
        var polyline = new Polyline
        {
            StrokeColor = routeColor,
            StrokeWidth = 22f
        };
        foreach (var coord in routeData.DecodedPoints) polyline.Geopath.Add(coord);

        return new RouteUIData
        {
            EncodedPolyline = routeData.EncodedPolyline,
            MapLine = polyline,
            DistanceKm = routeData.DistanceKm.ToString(),
            EtaText = routeData.EtaText,
            DecodedPoints = routeData.DecodedPoints,
            VoiceSteps = routeData.VoiceSteps ?? new List<RouteStep>()
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
                UpdatedLastAnnouncedTurn = currentLastAnnouncedTurn
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
                    activeRouteSteps.RemoveAt(0); // Engine safely pops it off the list by reference!
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

                    string instrLower = instruction.ToLower();
                    if (instrLower.Contains("turn left")) result.NextTurnIcon = "⬅️";
                    else if (instrLower.Contains("turn right")) result.NextTurnIcon = "➡️";
                    else if (instrLower.Contains("u-turn")) result.NextTurnIcon = "↩️";
                    else if (instrLower.Contains("exit")) result.NextTurnIcon = "↗️";
                    else result.NextTurnIcon = "⬆️";
                }
            }

            return result;
        }, cancellationToken);
    }
    public Location CalculateDynamicMeetupPoint(List<Location> currentRoute, List<Location> riderLocations, int currentRouteIndex)
    {
        if (currentRoute == null || currentRoute.Count == 0 || riderLocations.Count == 0) return null;

        bool isAnyoneLost = false;

        // 1. Is anyone actually off the blue line?
        foreach (var loc in riderLocations)
        {
            bool isOnPath = false;
            // Check every 5th coordinate to see if they are on the road (150m buffer)
            for (int i = 0; i < currentRoute.Count; i += 5)
            {
                if (Location.CalculateDistance(loc, currentRoute[i], DistanceUnits.Kilometers) < 0.15)
                {
                    isOnPath = true;
                    break;
                }
            }

            if (!isOnPath) { isAnyoneLost = true; break; }
        }

        // 2. If no one is lost, return null (meaning: clear the meetup pin)
        if (!isAnyoneLost) return null;

        // 3. THE FIX: Anchor to current progress, not the start of the route!
        // Start safely at 0 if the index is somehow invalid
        int targetIndex = Math.Max(0, currentRouteIndex);
        double accumulatedDist = 0;

        // Project 5km ahead of the Lead rider
        while (targetIndex < currentRoute.Count - 1 && accumulatedDist < 5.0)
        {
            accumulatedDist += Location.CalculateDistance(currentRoute[targetIndex], currentRoute[targetIndex + 1], DistanceUnits.Kilometers);
            targetIndex++;
        }

        // 4. THE SAFEGUARD: If we ran out of route before hitting 5km, just use the destination!
        return currentRoute[targetIndex];
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