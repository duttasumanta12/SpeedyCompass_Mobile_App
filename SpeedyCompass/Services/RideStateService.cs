using SpeedyCompass.Shared.Models;
using SpeedyCompass.Engines;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
        public ConcurrentDictionary<string, Location> OtherRiderLocations { get; set; } = new();
        public ConcurrentDictionary<string, double> OtherRiderSpeeds { get; set; } = new();

        // --- 5. TELEMETRY COOLDOWNS ---
        public DateTime LastSpeedAlert { get; set; } = DateTime.MinValue;
        public DateTime LastArrivalAlert { get; set; } = DateTime.MinValue;
        public DateTime LastSplinterAlert { get; set; } = DateTime.MinValue;
        public DateTime LastLagAlert { get; set; } = DateTime.MinValue;
        public double LastGroupPitstopKm { get; set; } = 0;
        public Location LastBroadcastLocation { get; set; } = null;
        public int CurrentRouteIndex { get; set; } = 0;
        public bool RunningInBackground { get; internal set; }
        public int OffRouteStrikeCount { get; set; } = 0;
        public List<Location> DrivenBreadcrumbs { get; set; } = new List<Location>();
        public double TopSpeedKmh { get; set; } = 0;
        public double? LastAnnouncedElevation { get; set; } = null;
        public DateTime? StopStartTime { get; set; } = null;
        public DateTime LastAutoPausePromptTime { get; set; } = DateTime.MinValue;
        public DateTime LastTrafficAlertTime { get; set; } = DateTime.MinValue;
        public List<SpeedInterval> CurrentTrafficData { get; set; } = new();

        // NEW: traffic refresh state
        public DateTime LastTrafficRefreshTime { get; set; } = DateTime.MinValue;
        public int LastTrafficRefreshRouteIndex { get; set; } = 0;

        // ==========================================
        // PHASE 1: DURABLE SNAPSHOT
        // ==========================================
        private string SnapshotFilePath => Path.Combine(FileSystem.AppDataDirectory, "ride_snapshot.json");

        public async Task SaveSnapshotAsync()
        {
            try
            {
                List<Location> breadcrumbsSnapshot;

                // THE FIX: Take a locked copy before JSON serialization runs!
                lock (this.DrivenBreadcrumbs)
                {
                    breadcrumbsSnapshot = this.DrivenBreadcrumbs.ToList();
                }
                // 1. Map to strict DTO
                var dto = new RideSnapshotDto
                {
                    CurrentSettings = this.CurrentSettings,
                    MyRole = this.MyRole,
                    ActiveDestination = this.ActiveDestination,
                    ActiveDestinationName = this.ActiveDestinationName,
                    ActiveMeetupPoint = this.ActiveMeetupPoint,
                    CurrentRoutePoints = this.CurrentRoutePoints,
                    CurrentRouteIndex = this.CurrentRouteIndex,
                    CumulativeDistanceKm = this.CumulativeDistanceKm,
                    LastOdometerLocation = this.LastOdometerLocation,
                    MaxSpeedKmh = this.MaxSpeedKmh,
                    TopSpeedKmh = this.TopSpeedKmh,
                    DrivenBreadcrumbs = breadcrumbsSnapshot,

                    // Convert ConcurrentDictionary to standard Dictionary for safe serialization
                    OtherRiderLocations = new Dictionary<string, Location>(this.OtherRiderLocations)
                };

                // 2. Safely write to disk
                var tempFile = SnapshotFilePath + ".tmp";
                using var stream = File.Create(tempFile);
                await JsonSerializer.SerializeAsync(stream, dto);
                stream.Close();

                File.Move(tempFile, SnapshotFilePath, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Snapshot Error] Failed to save state: {ex.Message}");
            }
        }

        public async Task LoadSnapshotAsync()
        {
            try
            {
                if (!File.Exists(SnapshotFilePath)) return;

                using var stream = File.OpenRead(SnapshotFilePath);
                var snapshot = await JsonSerializer.DeserializeAsync<RideSnapshotDto>(stream);

                if (snapshot != null)
                {
                    // 1. Rehydrate critical persistent properties
                    this.CurrentSettings = snapshot.CurrentSettings;
                    this.MyRole = snapshot.MyRole ?? "Rider";
                    this.ActiveDestination = snapshot.ActiveDestination;
                    this.ActiveDestinationName = snapshot.ActiveDestinationName;
                    this.ActiveMeetupPoint = snapshot.ActiveMeetupPoint;
                    this.CurrentRoutePoints = snapshot.CurrentRoutePoints ?? new();
                    this.CurrentRouteIndex = snapshot.CurrentRouteIndex;
                    this.CumulativeDistanceKm = snapshot.CumulativeDistanceKm;
                    this.LastOdometerLocation = snapshot.LastOdometerLocation;
                    this.MaxSpeedKmh = snapshot.MaxSpeedKmh;
                    this.TopSpeedKmh = snapshot.TopSpeedKmh;
                    this.DrivenBreadcrumbs = snapshot.DrivenBreadcrumbs ?? new();

                    // Restore offline pins
                    this.OtherRiderLocations.Clear();
                    if (snapshot.OtherRiderLocations != null)
                    {
                        foreach (var kvp in snapshot.OtherRiderLocations)
                            this.OtherRiderLocations[kvp.Key] = kvp.Value;
                    }

                    // 2. THE FIX: Explicitly zero out dependent/ephemeral state!
                    // This prevents stale alerts or frozen speedometer values on resume.
                    this.OtherRiderSpeeds.Clear();
                    this.RunningInBackground = false;
                    this.TotalStoppedTime = TimeSpan.Zero;
                    this.LastStopTime = null;

                    // Reset all network & notification cooldowns
                    this.LastSpeedAlert = DateTime.MinValue;
                    this.LastArrivalAlert = DateTime.MinValue;
                    this.LastSplinterAlert = DateTime.MinValue;
                    this.LastLagAlert = DateTime.MinValue;
                    this.LastBroadcastLocation = null;
                    this.LastTrafficAlertTime = DateTime.MinValue;
                    this.LastTrafficRefreshTime = DateTime.MinValue;
                    this.LastTrafficRefreshRouteIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Snapshot Error] Failed to load state: {ex.Message}");
            }
        }

        public void ClearSnapshot()
        {
            if (File.Exists(SnapshotFilePath)) File.Delete(SnapshotFilePath);
        }

        // ==========================================
        // EDGE CASE RESET HANDLERS
        // ==========================================
        public void HardResetAll()
        {
            ResetNavigationState();
            ResetTelemetryState();
            ClearSnapshot(); // Wipe the disk on a hard reset
        }

        public void ResetNavigationState()
        {
            ActiveDestination = null;
            ActiveDestinationName = string.Empty;
            ActiveMeetupPoint = null;
            CurrentRoutePoints.Clear();
            LastRerouteTime = DateTime.MinValue;
            CurrentRouteIndex = 0;
        }

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
            OffRouteStrikeCount = 0;
            TopSpeedKmh = 0;

            LastTrafficAlertTime = DateTime.MinValue;
            LastTrafficRefreshTime = DateTime.MinValue;
            LastTrafficRefreshRouteIndex = 0;
            CurrentTrafficData.Clear();
        }

        public bool ShouldBroadcastLocation(Location currentLocation, double speedKmh)
        {
            if (LastBroadcastLocation == null) return true;

            int minDist = CurrentSettings?.MinUpdateDistanceMeters ?? 10;
            int maxDist = CurrentSettings?.MaxUpdateDistanceMeters ?? 100;

            double speedRatio = Math.Clamp(speedKmh / 120.0, 0.0, 1.0);
            double dynamicThresholdMeters = minDist + ((maxDist - minDist) * speedRatio);
            double distTraveledMeters = Location.CalculateDistance(LastBroadcastLocation, currentLocation, DistanceUnits.Kilometers) * 1000;

            return distTraveledMeters >= dynamicThresholdMeters;
        }
    }
    // --- PHASE 1: DURABLE SNAPSHOT DTO ---
    public class RideSnapshotDto
    {
        // The exact version of the snapshot structure (for future migrations)
        public int SnapshotVersion { get; set; } = 1;

        public GroupSettingsDto CurrentSettings { get; set; }
        public string MyRole { get; set; }

        public Location ActiveDestination { get; set; }
        public string ActiveDestinationName { get; set; }
        public Location ActiveMeetupPoint { get; set; }
        public List<Location> CurrentRoutePoints { get; set; }
        public int CurrentRouteIndex { get; set; }

        public double CumulativeDistanceKm { get; set; }
        public Location LastOdometerLocation { get; set; }
        public double MaxSpeedKmh { get; set; }
        public double TopSpeedKmh { get; set; }
        public List<Location> DrivenBreadcrumbs { get; set; }

        // We save the last known locations so the map isn't completely empty 
        // while waiting for the network, but we drop their speeds.
        public Dictionary<string, Location> OtherRiderLocations { get; set; }
    }
}