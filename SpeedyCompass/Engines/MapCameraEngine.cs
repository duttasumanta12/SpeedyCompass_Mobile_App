using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace SpeedyCompass.Engines;

public class MapCameraEngine
{
    public MapSpan CalculateBoundingRegion(List<Location> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0) throw new NullReferenceException();
        if (targetPoints.Count == 1) return MapSpan.FromCenterAndRadius(targetPoints.First(), Distance.FromKilometers(0.5));

        double minLat = double.MaxValue, minLng = double.MaxValue;
        double maxLat = double.MinValue, maxLng = double.MinValue;

        foreach (var loc in targetPoints)
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

        return new MapSpan(new Location(centerLat, centerLng), latDistance, lngDistance);
    }
}