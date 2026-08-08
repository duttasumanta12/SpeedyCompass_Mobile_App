using SpeedyCompass.Models;

namespace SpeedyCompass.Services;

public interface IPlaceDiscoveryService
{
    Task<IReadOnlyList<PlaceResult>> SearchPlacesAsync(Location currentLocation, IReadOnlyList<Location>? activeRoutePoints, IReadOnlyList<string> placeTypes, CancellationToken cancellationToken = default);
}
