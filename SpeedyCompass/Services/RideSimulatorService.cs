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
    Chaos_Telemetry_Spikes,

    // --- NEW COPILOT SCENARIOS ---
    Mountain_Pass_Elevation,    // Tests 100m Altitude Climb Announcements
    Crash_Emergency_Protocol,   // Tests 10-Second Crash Fuse
    Long_Stop_AutoPause         // Tests 45-Second Idle Banner
}

public class RideSimulatorService
{
    private readonly RideStateService _rideCache;
    private readonly IRoutingEngine _routingEngine;
    private readonly ILocationTracker _locationTracker;

    private readonly SemaphoreSlim _simGate = new(1, 1);
    private bool _isSimulating = false;

    public Action<Location, double, double>? OnLocationGenerated;

    // THE FIX: A mock trigger since the simulator can't physically shake the phone
    public Action? OnSimulatedCrash;

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

    public async Task StartSimulationAsync(Func<GroupState> getLiveState, CancellationToken cancellationToken, RideScenario scenario = RideScenario.Baseline_Navigate_Clean, int seed = 42)
    {
        if (_rideCache.CurrentRoutePoints == null || _rideCache.CurrentRoutePoints.Count == 0) return;

        if (!await _simGate.WaitAsync(0)) return;

        try
        {
            await Task.Delay(2000, cancellationToken);
            _isSimulating = true;
            if (_locationTracker != null) _locationTracker.IsSimulating = true;

            var simulationPath = _rideCache.CurrentRoutePoints.ToList();
            int currentIndex = Math.Max(0, _rideCache.CurrentRouteIndex);
            if (currentIndex >= simulationPath.Count) currentIndex = simulationPath.Count - 1;

            var rnd = new Random(seed);

            int deviationIndex = (scenario == RideScenario.WrongTurn_Reroute_Recovery) ? Math.Max(5, simulationPath.Count / 5) : -1;
            if (deviationIndex <= currentIndex) deviationIndex = -1;

            bool isCurrentlyDeviating = false;
            bool hasTriggeredStrikes = false;

            // --- COPILOT TRACKERS ---
            double simulatedAltitude = 100.0; // Baseline starting altitude
            int stoppedTicks = 0;             // Counts how long we are sitting still

            double currentSimHeading = 0;
            Location currentSimLoc = simulationPath[currentIndex];

            AppLogger.Info("Simulator", $"Starting {scenario} from index {currentIndex}/{simulationPath.Count} (Seed: {seed})");

            while (currentIndex < simulationPath.Count && _isSimulating && !cancellationToken.IsCancellationRequested)
            {
                var currentState = getLiveState();

                if (currentState < GroupState.Navigating) break;
                if (currentState > GroupState.Navigating && currentState < GroupState.Completed)
                {
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }

                double speedKmh = 60;
                double accuracy = 5;
                int delayMs = 2000;

                // THE FIX: Controls if we physically move forward on the map line. 
                // If false, we stay parked, but still fire GPS events!
                bool advanceIndex = true;

                switch (scenario)
                {
                    case RideScenario.StopGo_CityTraffic:
                        speedKmh = rnd.Next(0, 100) < 30 ? 0 : rnd.Next(15, 45);
                        if (speedKmh == 0) advanceIndex = false;
                        break;

                    case RideScenario.Tunnel_GpsLoss_Recover:
                        int tunnelStart = simulationPath.Count / 5;
                        if (currentIndex > tunnelStart && currentIndex < tunnelStart + 10)
                        {
                            AppLogger.Info("Simulator", "🚇 Entering tunnel. Freezing GPS.");
                            accuracy = 150;
                            advanceIndex = false; // Freeze position
                        }
                        break;

                    case RideScenario.Chaos_Telemetry_Spikes:
                        speedKmh = rnd.Next(10, 140);
                        if (rnd.Next(0, 10) == 0) accuracy = rnd.Next(50, 500);
                        break;

                    case RideScenario.Mountain_Pass_Elevation:
                        // Climb rapidly: 25 meters per tick (2 seconds). 
                        // It will hit the 100m voice-alert threshold every 8 seconds!
                        simulatedAltitude += 25.0;
                        break;

                    case RideScenario.Long_Stop_AutoPause:
                        // Drive normally for 15 steps, then stop for 50 seconds (25 ticks)
                        if (currentIndex == 15 && stoppedTicks < 25)
                        {
                            speedKmh = 0;
                            advanceIndex = false; // Don't move on the map
                            stoppedTicks++;
                            AppLogger.Info("Simulator", $"Idle at stoplight... {(stoppedTicks * 2)}s");
                        }
                        break;

                    case RideScenario.Crash_Emergency_Protocol:
                        // Drive for 20 steps, then CRASH
                        if (currentIndex == 20)
                        {
                            if (stoppedTicks == 0)
                            {
                                AppLogger.Info("Simulator", "💥 SIMULATING MASSIVE G-FORCE IMPACT!");
                                OnSimulatedCrash?.Invoke();
                            }
                            speedKmh = 0;
                            advanceIndex = false; // We are wrecked, we aren't moving.
                            stoppedTicks++;
                        }
                        break;
                }

                // 3. WRONG TURN / DEVIATION ENGINE
                if (currentIndex == deviationIndex && !isCurrentlyDeviating)
                {
                    AppLogger.Info("Simulator", "⚠️ INITIATING WRONG TURN...");
                    isCurrentlyDeviating = true;
                    hasTriggeredStrikes = false;

                    if (currentIndex < simulationPath.Count - 1)
                        currentSimHeading = CalculateBearing(simulationPath[currentIndex], simulationPath[currentIndex + 1]);

                    double detourHeading = (currentSimHeading + 45) % 360;
                    var fakePath = new List<Location>();
                    var devLoc = currentSimLoc;

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

                currentSimLoc = simulationPath[currentIndex];

                if (scenario == RideScenario.Chaos_Telemetry_Spikes && rnd.Next(0, 5) == 0)
                {
                    currentSimLoc.Latitude += (rnd.NextDouble() - 0.5) * 0.0005;
                    currentSimLoc.Longitude += (rnd.NextDouble() - 0.5) * 0.0005;
                }

                if (currentIndex < simulationPath.Count - 1)
                    currentSimHeading = CalculateBearing(simulationPath[currentIndex], simulationPath[currentIndex + 1]);

                // THE FIX: Only advance the index if we aren't stopped/crashed/in a tunnel!
                if (advanceIndex)
                {
                    currentIndex++;
                }

                var point = new Location(currentSimLoc.Latitude, currentSimLoc.Longitude)
                {
                    Course = currentSimHeading,
                    Speed = speedKmh / 3.6, // MAUI expects m/s
                    Accuracy = accuracy,
                    Altitude = simulatedAltitude, // THE FIX: Inject simulated elevation
                    Timestamp = DateTimeOffset.UtcNow
                };

                try
                {
                    OnLocationGenerated?.Invoke(point, speedKmh, currentSimHeading);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Simulator", ex, "Downstream GPS listener threw an exception.");
                }

                if (isCurrentlyDeviating)
                {
                    if (_rideCache.OffRouteStrikeCount > 0) hasTriggeredStrikes = true;
                    if (hasTriggeredStrikes && _rideCache.OffRouteStrikeCount == 0)
                    {
                        AppLogger.Info("Simulator", "✅ REROUTE CAUGHT!");
                        simulationPath = _rideCache.CurrentRoutePoints.ToList();
                        currentIndex = _rideCache.CurrentRouteIndex;
                        isCurrentlyDeviating = false;
                        deviationIndex = -1;
                    }
                }

                await Task.Delay(delayMs, cancellationToken);
            }
        }
        catch (TaskCanceledException) { AppLogger.Info("Simulator", "Simulation manually cancelled."); }
        catch (Exception ex) { AppLogger.Error("Simulator", ex, "Simulation crashed."); }
        finally
        {
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