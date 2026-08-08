using Microsoft.Maui.Controls.Maps;
using SpeedyCompass.Models;
using Microsoft.Maui.Maps;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace SpeedyCompass.Services;

public class MapPoiManager
{
    private readonly Map _liveMap;
    private readonly RideStateService _rideCache;
    private readonly IPlaceDiscoveryService _placeDiscoveryService;
    private readonly List<Pin> _temporaryPoiPins = new();

    public Action OnPoisRendered;
    public Action OnPoisCleared;

    public MapPoiManager(Map liveMap, RideStateService rideCache, IPlaceDiscoveryService placeDiscoveryService)
    {
        _liveMap = liveMap;
        _rideCache = rideCache;
        _placeDiscoveryService = placeDiscoveryService;
    }

    public async Task SearchAndRenderPoisAsync(Location currentLoc, List<string> placeTypes, string emoji)
    {
        if (currentLoc == null || _placeDiscoveryService == null) return;

        try
        {
            var activePoints = _rideCache?.CurrentRoutePoints;
            var places = await _placeDiscoveryService.SearchPlacesAsync(currentLoc, activePoints, placeTypes);

            if (places.Count > 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ClearTemporaryPois(); // Clean slate

                    foreach (var place in places)
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
                        _liveMap.Pins.Add(pin);
                    }

                    OnPoisRendered?.Invoke(); // Tell the UI to show the "Clear" button
                });

                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(10));
                    ClearTemporaryPois();
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"POI Fetch Error: {ex.Message}");
        }
    }

    public void ClearTemporaryPois()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var oldPin in _temporaryPoiPins)
            {
                _liveMap.Pins.Remove(oldPin);
            }
            _temporaryPoiPins.Clear();

            OnPoisCleared?.Invoke(); // Tell the UI to hide the "Clear" button
        });
    }
}