using SpeedyCompass.Engines;
using SpeedyCompass.Models;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Services;

public class RideSimulatorService
{
    private readonly RideStateService _rideCache;
    private readonly IRoutingEngine _routingEngine;
    private readonly ILocationTracker locationTracker;
    private bool _isSimulating = false;

    // We use Action delegates so the simulator can trigger the exact same code the real GPS does!
    public Action<Location, double, double> OnLocationGenerated;
    public Action OnSimulationEnded;

    public RideSimulatorService(RideStateService rideCache, IRoutingEngine routingEngine, ILocationTracker locationTracker)
    {
        _rideCache = rideCache;
        _routingEngine = routingEngine;
        this.locationTracker = locationTracker;
    }

    public void StopSimulation()
    {
        _isSimulating = false;
        locationTracker?.IsSimulating = false;
    }

    public async Task StartSimulationAsync(GroupState currentState, CancellationToken cancellationToken, bool triggerDeviationTest = true)
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count == 0) return;
        if (_isSimulating) return;

        await Task.Delay(2000);
        _isSimulating = true;
        locationTracker.IsSimulating = true;

        var simulationPath = _rideCache.CurrentRoutePoints.ToList();
        int currentIndex = 0;

        int deviationIndex = triggerDeviationTest ? Math.Max(5, simulationPath.Count / 5) : -1;
        bool isCurrentlyDeviating = false;
        double currentSimHeading = 0;
        Location currentSimLoc = simulationPath[0];

        AppLogger.Info("Simulator", $"Starting route simulation. Deviation Test: {triggerDeviationTest}");

        while (currentIndex < simulationPath.Count)
        {
            if (currentState != GroupState.Navigating || !_isSimulating || cancellationToken.IsCancellationRequested)
            {
                _isSimulating = false;
                break;
            }

            double speedKmh = 60; // Hardcoded default for testing

            if (speedKmh == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            // --- 1. Deviation Generator ---
            if (currentIndex == deviationIndex && !isCurrentlyDeviating)
            {
                AppLogger.Info("Simulator", "⚠️ INITIATING DEVIATION TEST. Fetching detour route...");
                isCurrentlyDeviating = true;

                if (currentIndex < simulationPath.Count - 1)
                    currentSimHeading = CalculateBearing(simulationPath[currentIndex], simulationPath[currentIndex + 1]);

                double detourHeading = (currentSimHeading + 90) % 360;
                double distMeters = 2000.0;
                double latOffset = (distMeters * Math.Cos(detourHeading * Math.PI / 180.0)) / 111111.0;
                double lngOffset = (distMeters * Math.Sin(detourHeading * Math.PI / 180.0)) / (111111.0 * Math.Cos(simulationPath[currentIndex].Latitude * Math.PI / 180.0));

                var fakeDetourDestination = new Location(simulationPath[currentIndex].Latitude + latOffset, simulationPath[currentIndex].Longitude + lngOffset);

                try
                {
                    var detourResult = await _routingEngine.GetRouteDataAsync(simulationPath[currentIndex], fakeDetourDestination);

                    if (detourResult != null && detourResult.DecodedPoints.Count > 0)
                    {
                        AppLogger.Info("Simulator", "✅ Real-road detour fetched! Swapping simulator tracks.");
                        simulationPath = detourResult.DecodedPoints.ToList();
                        currentIndex = 0;
                    }
                }
                catch (Exception ex) { AppLogger.Error("Simulator", ex, "Failed to fetch real-road detour."); }
            }

            currentSimLoc = simulationPath[currentIndex];
            if (currentIndex < simulationPath.Count - 1)
            {
                currentSimHeading = CalculateBearing(simulationPath[currentIndex], simulationPath[currentIndex + 1]);
            }
            currentIndex++;

            var point = new Location(currentSimLoc.Latitude, currentSimLoc.Longitude)
            {
                Course = currentSimHeading,
                Speed = speedKmh / 3.6,
                Accuracy = 5,
                Timestamp = DateTimeOffset.UtcNow
            };

            int delayMs = 2000; // Simulated delay

            // Fire the location back to LobbyPage so it can run the exact same UI updates and network broadcasts as the real hardware!
            OnLocationGenerated?.Invoke(point, speedKmh, currentSimHeading);

            // --- 2. Reroute Snapper ---
            if (isCurrentlyDeviating && _rideCache.OffRouteStrikeCount == 0 && _rideCache.CurrentRoutePoints.Count > 0)
            {
                double distToNewRoute = Location.CalculateDistance(point, _rideCache.CurrentRoutePoints[0], DistanceUnits.Kilometers);
                if (distToNewRoute < 0.3)
                {
                    AppLogger.Info("Simulator", "✅ REROUTE CAUGHT! Snapping simulator to new route.");
                    simulationPath = _rideCache.CurrentRoutePoints.ToList();
                    currentIndex = 0;
                    isCurrentlyDeviating = false;
                    deviationIndex = -1;
                }
            }

            await Task.Delay(delayMs);
        }

        _isSimulating = false;
        locationTracker?.IsSimulating = false;
        OnSimulationEnded?.Invoke();
        AppLogger.Info("Simulator", "Simulation ended cleanly.");
    }

    private double CalculateBearing(Location start, Location end)
    {
        double lat1 = start.Latitude * (Math.PI / 180.0);
        double lon1 = start.Longitude * (Math.PI / 180.0);
        double lat2 = end.Latitude * (Math.PI / 180.0);
        double lon2 = end.Longitude * (Math.PI / 180.0);

        double dLon = lon2 - lon1;
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);

        double bearing = Math.Atan2(y, x) * (180.0 / Math.PI);
        return (bearing + 360.0) % 360.0;
    }
}