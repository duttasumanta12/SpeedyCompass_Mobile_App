using System.Text.Json;
using SpeedyCompass.Models;

namespace SpeedyCompass.Engines;

// A clean wrapper to pass data back to the UI
public class RouteCalculationResult
{
    public string EncodedPolyline { get; set; }
    public List<Location> DecodedPoints { get; set; } = new();
    public List<RouteStep> VoiceSteps { get; set; } = new();
}

public interface IRoutingEngine
{
    Task<RouteCalculationResult> GetRouteDataAsync(Location origin, Location dest, Location meetup = null, bool includeVoiceSteps = false);
    List<Location> DecodeGooglePolyline(string encodedPoints);
    string EncodeLocationList(List<Location> points);
}

public class RoutingEngine : IRoutingEngine
{
    private readonly HttpClient _httpClient;
    private readonly string _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk"; // Consider moving this to a secure config later!

    public RoutingEngine()
    {
        _httpClient = new HttpClient();
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

            string fieldMask = "routes.polyline.encodedPolyline";
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