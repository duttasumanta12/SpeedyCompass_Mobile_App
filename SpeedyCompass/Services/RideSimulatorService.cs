using SpeedyCompass.Engines;
using SpeedyCompass.Models;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Services;

public enum RideScenario
{
    Baseline_Navigate_Clean,
    WrongTurn_Reroute_Recovery,
    StopGo_CityTraffic,
    Tunnel_GpsLoss_Recover,
    Chaos_Telemetry_Spikes
}

public class RideSimulatorService
{
    private readonly RideStateService _rideCache;
    private readonly IRoutingEngine _routingEngine;
    private readonly ILocationTracker _locationTracker;

    // THE FIX: Thread-safe locking to prevent double-starts
    private readonly SemaphoreSlim _simGate = new(1, 1);
    private bool _isSimulating = false;

    public Action<Location, double, double>? OnLocationGenerated;
    public Action? OnSimulationEnded;

    public RideSimulatorService(RideStateService rideCache, IRoutingEngine routingEngine, ILocationTracker locationTracker)
    {
        _rideCache = rideCache;
        _routingEngine = routingEngine;
        _locationTracker = locationTracker;
    }

    public void StopSimulation()
    {
        _isSimulating = false;
        if (_locationTracker != null) _locationTracker.IsSimulating = false;
    }

    // THE FIX: Pass `Func<GroupState>` so the simulator always evaluates the LIVE state, not a stale value!
    public async Task StartSimulationAsync(Func<GroupState> getLiveState, CancellationToken cancellationToken, RideScenario scenario = RideScenario.Baseline_Navigate_Clean, int seed = 42)
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count == 0) return;

        // Prevent race conditions if Admin spams the "Start" button
        if (!await _simGate.WaitAsync(0)) return;

        try
        {
            await Task.Delay(2000, cancellationToken);
            _isSimulating = true;
            if (_locationTracker != null) _locationTracker.IsSimulating = true;

            var simulationPath = _rideCache.CurrentRoutePoints.ToList();
            int currentIndex = Math.Max(0, _rideCache.CurrentRouteIndex);
            // Safety bound check
            if (currentIndex >= simulationPath.Count) currentIndex = simulationPath.Count - 1;
            var rnd = new Random(seed); // Deterministic randomness

            int deviationIndex = (scenario == RideScenario.WrongTurn_Reroute_Recovery) ? Math.Max(5, simulationPath.Count / 5) : -1;

            // If we resumed the simulation AFTER the planned deviation point, cancel the deviation.
            if (deviationIndex <= currentIndex) deviationIndex = -1;

            bool isCurrentlyDeviating = false;
            bool hasTriggeredStrikes = false;

            double currentSimHeading = 0;
            // Start the physical location at the resumed index!
            Location currentSimLoc = simulationPath[currentIndex];

            AppLogger.Info("Simulator", $"Starting {scenario} from index {currentIndex}/{simulationPath.Count} (Seed: {seed})");

            while (currentIndex < simulationPath.Count && _isSimulating && !cancellationToken.IsCancellationRequested)
            {
                // 1. THE FIX: Always check the absolute latest state from the memory reference
                var currentState = getLiveState();

                if (currentState < GroupState.Navigating)
                {
                    break; // The ride was reset/canceled
                }

                if (currentState > GroupState.Navigating && currentState < GroupState.Completed)
                {
                    // The ride is Paused! Wait patiently without advancing the GPS point.
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }

                // 2. SCENARIO PROFILES (Knobs & Modifiers)
                double speedKmh = 60;
                double accuracy = 5;
                int delayMs = 2000;

                switch (scenario)
                {
                    case RideScenario.StopGo_CityTraffic:
                        // 30% chance to be stopped at a light, otherwise 15-45 km/h
                        speedKmh = rnd.Next(0, 100) < 30 ? 0 : rnd.Next(15, 45);
                        break;

                    case RideScenario.Tunnel_GpsLoss_Recover:
                        // Simulate entering a tunnel 20% of the way into the ride
                        int tunnelStart = simulationPath.Count / 5;
                        if (currentIndex > tunnelStart && currentIndex < tunnelStart + 10)
                        {
                            AppLogger.Info("Simulator", "🚇 Entering tunnel. Freezing GPS and degrading accuracy.");
                            accuracy = 150; // Terrible accuracy
                            // We don't advance currentIndex to simulate lost signal
                            await Task.Delay(delayMs, cancellationToken);
                            continue;
                        }
                        break;

                    case RideScenario.Chaos_Telemetry_Spikes:
                        speedKmh = rnd.Next(10, 140); // Erratic speeds
                        if (rnd.Next(0, 10) == 0) accuracy = rnd.Next(50, 500); // Random accuracy spikes
                        break;
                }

                if (speedKmh == 0)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                // 3. WRONG TURN / DEVIATION ENGINE
                if (currentIndex == deviationIndex && !isCurrentlyDeviating)
                {
                    AppLogger.Info("Simulator", "⚠️ INITIATING WRONG TURN. Forcing bike off-road...");
                    isCurrentlyDeviating = true;
                    hasTriggeredStrikes = false;

                    if (currentIndex < simulationPath.Count - 1)
                        currentSimHeading = CalculateBearing(simulationPath[currentIndex], simulationPath[currentIndex + 1]);

                    double detourHeading = (currentSimHeading + 45) % 360;
                    var fakePath = new List<Location>();
                    var devLoc = currentSimLoc;

                    // Generate a 100-tick detour trajectory
                    for (int i = 0; i < 100; i++)
                    {
                        double distMeters = 30.0;
                        double latOffset = (distMeters * Math.Cos(detourHeading * Math.PI / 180.0)) / 111111.0;
                        double lngOffset = (distMeters * Math.Sin(detourHeading * Math.PI / 180.0)) / (111111.0 * Math.Cos(devLoc.Latitude * Math.PI / 180.0));

                        devLoc = new Location(devLoc.Latitude + latOffset, devLoc.Longitude + lngOffset);
                        fakePath.Add(devLoc);
                    }

                    simulationPath = fakePath;
                    currentIndex = 0;
                }

                // Get Current Location
                currentSimLoc = simulationPath[currentIndex];

                // Chaos Mode: Inject geographic jitter
                if (scenario == RideScenario.Chaos_Telemetry_Spikes && rnd.Next(0, 5) == 0)
                {
                    currentSimLoc.Latitude += (rnd.NextDouble() - 0.5) * 0.0005;
                    currentSimLoc.Longitude += (rnd.NextDouble() - 0.5) * 0.0005;
                }

                if (currentIndex < simulationPath.Count - 1)
                    currentSimHeading = CalculateBearing(simulationPath[currentIndex], simulationPath[currentIndex + 1]);

                currentIndex++;

                var point = new Location(currentSimLoc.Latitude, currentSimLoc.Longitude)
                {
                    Course = currentSimHeading,
                    Speed = speedKmh / 3.6, // MAUI expects meters/second
                    Accuracy = accuracy,
                    Timestamp = DateTimeOffset.UtcNow
                };

                // 4. THE FIX: Catch downstream UI/Map errors so they don't break the simulator loop
                try
                {
                    OnLocationGenerated?.Invoke(point, speedKmh, currentSimHeading);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Simulator", ex, "Downstream GPS listener threw an exception.");
                }

                // 5. THE TRIPWIRE SNAPPER (Reroute Recovery)
                if (isCurrentlyDeviating)
                {
                    if (_rideCache.OffRouteStrikeCount > 0) hasTriggeredStrikes = true;

                    if (hasTriggeredStrikes && _rideCache.OffRouteStrikeCount == 0)
                    {
                        AppLogger.Info("Simulator", "✅ REROUTE CAUGHT! Snapping simulator to the Splice Seam.");
                        simulationPath = _rideCache.CurrentRoutePoints.ToList();
                        currentIndex = _rideCache.CurrentRouteIndex; // Snap to seam
                        isCurrentlyDeviating = false;
                        deviationIndex = -1;
                    }
                }

                await Task.Delay(delayMs, cancellationToken);
            }
        }
        catch (TaskCanceledException)
        {
            AppLogger.Info("Simulator", "Simulation manually cancelled.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Simulator", ex, "Simulation crashed.");
        }
        finally
        {
            // 6. THE FIX: Bulletproof Cleanup. 
            // This runs guaranteed, even if the task was cancelled, broke, or completed cleanly.
            //_isSimulating = false;
            //if (_locationTracker != null) _locationTracker.IsSimulating = false;

            _simGate.Release();
            OnSimulationEnded?.Invoke();

            AppLogger.Info("Simulator", "Simulation teardown complete.");
        }
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