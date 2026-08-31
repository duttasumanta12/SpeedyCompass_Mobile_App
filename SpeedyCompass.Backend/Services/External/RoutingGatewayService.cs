using Microsoft.Extensions.Caching.Memory;
using SpeedyCompass.Shared.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeedyCompass.Backend.Services.External;

public class RoutingGatewayService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RoutingGatewayService> _logger;

    public RoutingGatewayService(HttpClient httpClient, IConfiguration config, IMemoryCache cache, ILogger<RoutingGatewayService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RouteBffResponse?> GetRouteAsync(RouteRequestDto req)
    {
        // RULE 1 & 2: Always use Mapbox for Previews OR Free Tier
        if (req.IsPreview || req.Tier == "Free")
        {
            _logger.LogInformation("Routing via Mapbox (Preview or Free Tier)");
            return await GetMapboxRouteAsync(req);
        }

        // RULE 3: Use Google Routes for Pro Tier Active Navigation
        _logger.LogInformation("Routing via Google Routes (Pro Tier)");
        return await GetGoogleRouteAsync(req);
    }

    // =====================================================================
    // 2. MAPBOX IMPLEMENTATION (Free & Previews)
    // =====================================================================
    private async Task<RouteBffResponse?> GetMapboxRouteAsync(RouteRequestDto req)
    {
        string cacheKey = $"mapbox_{Math.Round(req.OriginLat, 3)}_{Math.Round(req.OriginLng, 3)}_to_{Math.Round(req.DestLat, 3)}_{Math.Round(req.DestLng, 3)}_{req.IncludeVoiceSteps}";
        if (_cache.TryGetValue(cacheKey, out RouteBffResponse? cachedResponse)) return cachedResponse;

        string mapboxToken = _config["ExternalKeys:MapboxDirections"];

        // Mapbox coordinates are strictly Longitude,Latitude !
        var coords = new List<string> { $"{req.OriginLng},{req.OriginLat}" };
        if (req.MeetupLat.HasValue && req.MeetupLng.HasValue)
            coords.Add($"{req.MeetupLng.Value},{req.MeetupLat.Value}");
        coords.Add($"{req.DestLng},{req.DestLat}");

        string coordString = string.Join(";", coords);
        string stepsParam = req.IncludeVoiceSteps ? "true" : "false";

        // language=en forces English instructions!
        string url = $"https://api.mapbox.com/directions/v5/mapbox/driving/{coordString}?geometries=polyline&overview=full&steps={stepsParam}&language=en&access_token={mapboxToken}";

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0) return null;
        var route = routes[0];

        var result = new RouteBffResponse
        {
            EncodedPolyline = route.GetProperty("geometry").GetString(),
            DistanceKm = Math.Round(route.GetProperty("distance").GetDouble() / 1000.0, 1),
            EtaText = FormatEta(route.GetProperty("duration").GetDouble())
        };

        if (req.IncludeVoiceSteps && route.TryGetProperty("legs", out var legs))
        {
            foreach (var leg in legs.EnumerateArray())
            {
                if (leg.TryGetProperty("steps", out var steps))
                {
                    foreach (var step in steps.EnumerateArray())
                    {
                        var maneuver = step.GetProperty("maneuver");
                        var location = maneuver.GetProperty("location"); // [lng, lat]

                        result.VoiceSteps.Add(new RouteStepDto
                        {
                            Instruction = maneuver.GetProperty("instruction").GetString(),
                            TurnLng = location[0].GetDouble(),
                            TurnLat = location[1].GetDouble()
                        });
                    }
                }
            }
        }

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(15));
        return result;
    }

    // =====================================================================
    // 3. GOOGLE IMPLEMENTATION (Pro Tier Active Nav)
    // =====================================================================
    private async Task<RouteBffResponse?> GetGoogleRouteAsync(RouteRequestDto req)
    {
        string cacheKey = $"google_{Math.Round(req.OriginLat, 3)}_{Math.Round(req.OriginLng, 3)}_to_{Math.Round(req.DestLat, 3)}_{Math.Round(req.DestLng, 3)}_{req.IncludeVoiceSteps}";
        if (_cache.TryGetValue(cacheKey, out RouteBffResponse? cachedResponse)) return cachedResponse;

        string googleKey = _config["ExternalKeys:GoogleRoutes"];

        var requestBody = new
        {
            origin = new { location = new { latLng = new { latitude = req.OriginLat, longitude = req.OriginLng }, heading = req.OriginHeading } },
            destination = new { location = new { latLng = new { latitude = req.DestLat, longitude = req.DestLng } } },
            intermediates = req.MeetupLat.HasValue ? new[] { new { location = new { latLng = new { latitude = req.MeetupLat, longitude = req.MeetupLng } } } } : null,
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE_OPTIMAL",
            languageCode = "en-US" // <-- FORCES ENGLISH
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
        request.Headers.Add("X-Goog-Api-Key", googleKey);

        string fieldMask = "routes.polyline.encodedPolyline,routes.distanceMeters,routes.duration,routes.travelAdvisory.speedReadingIntervals";
        if (req.IncludeVoiceSteps) fieldMask += ",routes.legs.steps.startLocation,routes.legs.steps.navigationInstruction";
        request.Headers.Add("X-Goog-FieldMask", fieldMask);

        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var result = ParseGoogleResponseToBff(await response.Content.ReadAsStringAsync(), req.IncludeVoiceSteps);

        if (result != null) _cache.Set(cacheKey, result, TimeSpan.FromMinutes(15));
        return result;
    }

    public async Task<RouteBffResponse?> GetTrafficWindowAsync(RouteRequestDto req)
    {
        // Traffic is highly dynamic. Only cache for 60 seconds.
        string cacheKey = $"traffic_{Math.Round(req.OriginLat, 3)}_{Math.Round(req.OriginLng, 3)}";
        if (_cache.TryGetValue(cacheKey, out RouteBffResponse? cachedResponse)) return cachedResponse;

        string googleKey = _config["ExternalKeys:GoogleRoutes"];

        var requestBody = new
        {
            origin = new { location = new { latLng = new { latitude = req.OriginLat, longitude = req.OriginLng }, heading = req.OriginHeading } },
            destination = new { location = new { latLng = new { latitude = req.DestLat, longitude = req.DestLng } } },
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE_OPTIMAL",
            extraComputations = new[] { "TRAFFIC_ON_POLYLINE" },
            languageCode = "en-US"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
        request.Headers.Add("X-Goog-Api-Key", googleKey);
        request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline,routes.travelAdvisory.speedReadingIntervals");
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var result = ParseGoogleResponseToBff(await response.Content.ReadAsStringAsync(), false);

        if (result != null) _cache.Set(cacheKey, result, TimeSpan.FromSeconds(60));

        return result;
    }

    public async Task<RouteBffResponse?> GetMapboxOverviewAsync(List<double[]> routePoints)
    {
        if (routePoints == null || routePoints.Count < 2) return null;

        string mapboxToken = _config["ExternalKeys:MapboxDirections"];
        var coordString = string.Join(";", routePoints.Take(25).Select(p => $"{p[0]},{p[1]}"));

        string url = $"https://api.mapbox.com/directions/v5/mapbox/driving/{coordString}?geometries=polyline&overview=full&language=en&access_token={mapboxToken}";

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var route = doc.RootElement.GetProperty("routes")[0];

        return new RouteBffResponse
        {
            EncodedPolyline = route.GetProperty("geometry").GetString(),
            DistanceKm = Math.Round(route.GetProperty("distance").GetDouble() / 1000.0, 1),
            EtaText = $"{Math.Round(route.GetProperty("duration").GetDouble() / 60.0)}m"
        };
    }

    private RouteBffResponse ParseGoogleResponseToBff(string jsonString, bool includeVoiceSteps)
    {
        var result = new RouteBffResponse();
        using var doc = JsonDocument.Parse(jsonString);

        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
            return null;

        var mainRoute = routes[0];

        if (mainRoute.TryGetProperty("polyline", out var poly) && poly.TryGetProperty("encodedPolyline", out var enc))
            result.EncodedPolyline = enc.GetString();

        if (mainRoute.TryGetProperty("distanceMeters", out var dist))
            result.DistanceKm = Math.Round(dist.GetDouble() / 1000.0, 1);

        if (mainRoute.TryGetProperty("duration", out var dur))
        {
            var cleanSeconds = dur.GetString()?.Replace("s", "");
            if (double.TryParse(cleanSeconds, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double durationSec))
            {
                result.EtaText = FormatEta(durationSec);
            }
        }

        if (mainRoute.TryGetProperty("travelAdvisory", out var advisory) && advisory.TryGetProperty("speedReadingIntervals", out var intervals))
        {
            foreach (var interval in intervals.EnumerateArray())
            {
                result.TrafficData.Add(new SpeedIntervalDto
                {
                    StartIndex = interval.GetProperty("startPolylinePointIndex").GetInt32(),
                    EndIndex = interval.GetProperty("endPolylinePointIndex").GetInt32(),
                    Speed = interval.TryGetProperty("speed", out var s) ? s.GetString() : "NORMAL"
                });
            }
        }

        if (includeVoiceSteps && mainRoute.TryGetProperty("legs", out var legs))
        {
            foreach (var leg in legs.EnumerateArray())
            {
                if (leg.TryGetProperty("steps", out var steps))
                {
                    foreach (var step in steps.EnumerateArray())
                    {
                        if (step.TryGetProperty("navigationInstruction", out var nav) && step.TryGetProperty("startLocation", out var loc))
                        {
                            result.VoiceSteps.Add(new RouteStepDto
                            {
                                Instruction = nav.GetProperty("instructions").GetString(),
                                TurnLat = loc.GetProperty("latLng").GetProperty("latitude").GetDouble(),
                                TurnLng = loc.GetProperty("latLng").GetProperty("longitude").GetDouble()
                            });
                        }
                    }
                }
            }
        }
        return result;
    }
    private static string FormatEta(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.Hours > 0 ? $"{ts.Hours}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }
}