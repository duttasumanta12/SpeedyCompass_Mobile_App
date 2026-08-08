using Microsoft.Extensions.Configuration;
using SpeedyCompass.Engines;
using SpeedyCompass.Models;
using System.Text;
using System.Text.Json;

namespace SpeedyCompass.Services;

public class PlaceDiscoveryService : IPlaceDiscoveryService
{
    private static readonly HttpClient _httpClient = new();
    private readonly IRoutingEngine _routingEngine;
    private readonly string _googleApiKey;

    public PlaceDiscoveryService(IRoutingEngine routingEngine, IConfiguration configuration)
    {
        _routingEngine = routingEngine;
        _googleApiKey = configuration["GoogleApiKey"] ?? "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk";
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchPlacesAsync(Location currentLocation, IReadOnlyList<Location>? activeRoutePoints, IReadOnlyList<string> placeTypes, CancellationToken cancellationToken = default)
    {
        if (currentLocation == null || placeTypes == null || placeTypes.Count == 0)
            return [];

        try
        {
            bool hasActiveRoute = activeRoutePoints != null && activeRoutePoints.Count > 2;
            HttpRequestMessage request;

            if (hasActiveRoute)
            {
                string textQuery = string.Join(" OR ", placeTypes.Select(t => t.Replace("_", " ")));
                var upcomingPath = activeRoutePoints!.Take(500).ToList();
                string encodedPath = _routingEngine.EncodeLocationList(upcomingPath);

                var requestBody = new SearchTextRequest
                {
                    TextQuery = textQuery,
                    SearchAlongRouteParameters = new SearchAlongRouteParameters
                    {
                        Polyline = new RoutePolyline { EncodedPolyline = encodedPath }
                    }
                };

                request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var requestBody = new NearbySearchRequest
                {
                    IncludedTypes = [.. placeTypes],
                    MaxResultCount = 10,
                    LocationRestriction = new LocationRestriction
                    {
                        Circle = new SearchCircle
                        {
                            Center = new RouteLatLng { Latitude = currentLocation.Latitude, Longitude = currentLocation.Longitude },
                            Radius = 10000.0
                        }
                    }
                };

                request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchNearby")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };
            }

            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
            request.Headers.Add("X-Goog-FieldMask", "places.displayName,places.location,places.rating");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<NearbySearchResponse>(responseBody);
            return result?.Places ?? [];
        }
        catch
        {
            return [];
        }
    }
}
