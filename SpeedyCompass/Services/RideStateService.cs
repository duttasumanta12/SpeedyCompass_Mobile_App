using SpeedyCompass.Shared.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Services
{
    public class RideStateService
    {
        // --- 1. SETTINGS & IDENTIFICATION ---
        public GroupSettingsDto CurrentSettings { get; set; }
        public string MyRole { get; set; } = "Rider";

        // --- 2. NAVIGATION STATE ---
        public Location ActiveDestination { get; set; }
        public string ActiveDestinationName { get; set; }
        public Location ActiveMeetupPoint { get; set; }
        public List<Location> CurrentRoutePoints { get; set; } = new();
        public DateTime LastRerouteTime { get; set; } = DateTime.MinValue;

        // --- 3. PERSONAL TELEMETRY (Odometer & Time) ---
        public double CumulativeDistanceKm { get; set; } = 0;
        public Location LastOdometerLocation { get; set; } = null;
        public double MaxSpeedKmh { get; set; } = 0;
        public TimeSpan TotalStoppedTime { get; set; } = TimeSpan.Zero;
        public DateTime? LastStopTime { get; set; } = null;
        public DateTime RideStartTime { get; set; }

        // --- 4. LIVE CONVOY TRACKING ---
        public ConcurrentDictionary<string, Location> OtherRiderLocations { get; } = new();
        public ConcurrentDictionary<string, double> OtherRiderSpeeds { get; } = new();

        // --- 5. TELEMETRY COOLDOWNS ---
        public DateTime LastSpeedAlert { get; set; } = DateTime.MinValue;
        public DateTime LastArrivalAlert { get; set; } = DateTime.MinValue;
        public DateTime LastSplinterAlert { get; set; } = DateTime.MinValue;
        public DateTime LastLagAlert { get; set; } = DateTime.MinValue;
        public double LastGroupPitstopKm { get; set; } = 0;
        public Location LastBroadcastLocation { get; set; } = null;
        public int CurrentRouteIndex { get; set; } = 0;
        public bool RunningInBackground { get; internal set; }

        // ==========================================
        // EDGE CASE RESET HANDLERS
        // ==========================================

        // Called when the Admin completely cancels or finishes the route
        public void HardResetAll()
        {
            ResetNavigationState();
            ResetTelemetryState();
        }

        // Called when a brand new destination is set (Clears old polyline, keeps odometer)
        public void ResetNavigationState()
        {
            ActiveDestination = null;
            ActiveDestinationName = string.Empty;
            ActiveMeetupPoint = null;
            CurrentRoutePoints.Clear();
            LastRerouteTime = DateTime.MinValue;
            CurrentRouteIndex = 0;
        }

        // Called when we specifically want to zero out the Odometer/Speed trackers
        public void ResetTelemetryState()
        {
            CumulativeDistanceKm = 0;
            LastOdometerLocation = null;
            MaxSpeedKmh = 0;
            TotalStoppedTime = TimeSpan.Zero;
            LastStopTime = null;
            RideStartTime = DateTime.Now;

            LastSpeedAlert = DateTime.MinValue;
            LastArrivalAlert = DateTime.MinValue;
            LastSplinterAlert = DateTime.MinValue;
            LastLagAlert = DateTime.MinValue;
            LastGroupPitstopKm = 0;
        }
        public bool ShouldBroadcastLocation(Location currentLocation, double speedKmh)
        {
            if (LastBroadcastLocation == null) return true; // Always broadcast the very first point!

            int minDist = CurrentSettings?.MinUpdateDistanceMeters ?? 10;
            int maxDist = CurrentSettings?.MaxUpdateDistanceMeters ?? 100;

            // Ratio mapping: 0 km/h = 0.0, 120+ km/h = 1.0
            double speedRatio = Math.Clamp(speedKmh / 120.0, 0.0, 1.0);

            // Calculate the exact distance threshold based on current speed
            double dynamicThresholdMeters = minDist + ((maxDist - minDist) * speedRatio);

            double distTraveledMeters = Location.CalculateDistance(LastBroadcastLocation, currentLocation, DistanceUnits.Kilometers) * 1000;

            return distTraveledMeters >= dynamicThresholdMeters;
        }
    }
}
