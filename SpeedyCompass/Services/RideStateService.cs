using SpeedyCompass.Engines;
using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpeedyCompass.Services
{
    public class RideStateService
    {
        // --- 1. SETTINGS & IDENTIFICATION ---
        public GroupSettingsDto CurrentSettings { get; set; }
        public string MyRole { get; set; } = RiderRole.Rider.ToString();

        public RiderRole MyRoleEnum
        {
            get => RiderRoleParser.ParseOrDefault(MyRole);
            set => MyRole = value.ToString();
        }

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
        public double TopSpeedKmh { get; set; } = 0;
        public TimeSpan TotalStoppedTime { get; set; } = TimeSpan.Zero;
        public DateTime? LastStopTime { get; set; } = null;
        public DateTime RideStartTime { get; set; } = DateTime.UtcNow;

        // --- 4. LIVE CONVOY TRACKING ---
        public ConcurrentDictionary<string, Location> OtherRiderLocations { get; set; } = new();
        public ConcurrentDictionary<string, double> OtherRiderSpeeds { get; set; } = new();

        // --- 5. TELEMETRY COOLDOWNS & STATE ---
        public DateTime LastSpeedAlert { get; set; } = DateTime.MinValue;
        public DateTime LastArrivalAlert { get; set; } = DateTime.MinValue;
        public DateTime LastSplinterAlert { get; set; } = DateTime.MinValue;
        public DateTime LastLagAlert { get; set; } = DateTime.MinValue;
        public double LastGroupPitstopKm { get; set; } = 0;
        public Location LastBroadcastLocation { get; set; } = null;
        public int CurrentRouteIndex { get; set; } = 0;
        public bool RunningInBackground { get; internal set; }
        public int OffRouteStrikeCount { get; set; } = 0;
        public List<Location> DrivenBreadcrumbs { get; set; } = new();
        public double? LastAnnouncedElevation { get; set; } = null;
        public DateTime? StopStartTime { get; set; } = null;
        public DateTime LastAutoPausePromptTime { get; set; } = DateTime.MinValue;

        // --- 6. LIVE TRAFFIC STATE ---
        public DateTime LastTrafficAlertTime { get; set; } = DateTime.MinValue;
        public List<SpeedInterval> CurrentTrafficData { get; set; } = new();
        public DateTime LastTrafficRefreshTime { get; set; } = DateTime.MinValue;
        public int LastTrafficRefreshRouteIndex { get; set; } = 0;
        public RouteCalculationResult CachedMainRouteData { get; set; }
        // --- VISIBILITY & NETWORK STATE ---
        public HashSet<string> HiddenRiders { get; set; } = new();
        public HashSet<string> UsersWhoMutedMe { get; set; } = new();
        public HashSet<string> VisibilityInitialized { get; set; } = new();

        // --- NAVIGATION FLAGS ---
        public bool HaveIReachedMeetup { get; set; } = false;
        public bool HasAnnouncedArrival { get; set; } = false;
        public Location LastAnnouncedTurn { get; set; } = null;
        public HashSet<string> RidersAtMeetup { get; set; } = new();
        public GroupState? PendingCatchUpState { get; set; } = null;

        // --- SENSOR & TIMING COOLDOWNS ---
        public DateTime LastCrashEvent { get; set; } = DateTime.MinValue;
        public DateTime LastNetworkBroadcastTime { get; set; } = DateTime.MinValue;
        public List<Location> ActiveWaypoints { get; set; } = new();

        // ==========================================
        // PHASE 1: DURABLE SNAPSHOT
        // ==========================================
        private string SnapshotFilePath => Path.Combine(FileSystem.AppDataDirectory, "ride_snapshot.json");
        private readonly object _snapshotLock = new();

        public async Task SaveSnapshotAsync()
        {
            try
            {
                List<Location> breadcrumbsSnapshot;
                List<Location> routePointsSnapshot;
                List<SpeedInterval> trafficSnapshot;

                // 1. Thread-safe snapshots of all collections
                lock (this.DrivenBreadcrumbs)
                {
                    breadcrumbsSnapshot = this.DrivenBreadcrumbs.ToList();
                }

                lock (this.CurrentRoutePoints)
                {
                    routePointsSnapshot = this.CurrentRoutePoints.ToList();
                }

                lock (this.CurrentTrafficData)
                {
                    trafficSnapshot = this.CurrentTrafficData.ToList();
                }

                // 2. Map to strict DTO
                var dto = new RideSnapshotDto
                {
                    SnapshotVersion = 1,
                    CurrentSettings = this.CurrentSettings,
                    MyRole = this.MyRole,
                    ActiveDestination = this.ActiveDestination,
                    ActiveDestinationName = this.ActiveDestinationName,
                    ActiveMeetupPoint = this.ActiveMeetupPoint,
                    CurrentRoutePoints = routePointsSnapshot,
                    CurrentRouteIndex = this.CurrentRouteIndex,
                    CumulativeDistanceKm = this.CumulativeDistanceKm,
                    LastOdometerLocation = this.LastOdometerLocation,
                    MaxSpeedKmh = this.MaxSpeedKmh,
                    TopSpeedKmh = this.TopSpeedKmh,
                    DrivenBreadcrumbs = breadcrumbsSnapshot,

                    // Summary & Alert Continuity
                    RideStartTime = this.RideStartTime,
                    TotalStoppedTimeSeconds = this.TotalStoppedTime.TotalSeconds,
                    LastGroupPitstopKm = this.LastGroupPitstopKm,
                    LastAnnouncedElevation = this.LastAnnouncedElevation,

                    // Traffic Window Snapshot
                    CurrentTrafficData = trafficSnapshot,

                    // Safe copy of other riders
                    OtherRiderLocations = new Dictionary<string, Location>(this.OtherRiderLocations)
                };

                // 3. Isolated file write with explicit scoping to release file lock
                var tempFile = SnapshotFilePath + ".tmp";

                await using (var stream = File.Create(tempFile))
                {
                    await JsonSerializer.SerializeAsync(stream, dto);
                    await stream.FlushAsync();
                } // Stream is guaranteed closed/disposed here

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

                RideSnapshotDto snapshot;
                await using (var stream = File.OpenRead(SnapshotFilePath))
                {
                    snapshot = await JsonSerializer.DeserializeAsync<RideSnapshotDto>(stream);
                }

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

                    // Restore summary continuity
                    this.RideStartTime = snapshot.RideStartTime == default ? DateTime.UtcNow : snapshot.RideStartTime;
                    this.TotalStoppedTime = TimeSpan.FromSeconds(snapshot.TotalStoppedTimeSeconds);
                    this.LastGroupPitstopKm = snapshot.LastGroupPitstopKm;
                    this.LastAnnouncedElevation = snapshot.LastAnnouncedElevation;
                    this.CurrentTrafficData = snapshot.CurrentTrafficData ?? new();

                    // Restore offline pins
                    this.OtherRiderLocations.Clear();
                    if (snapshot.OtherRiderLocations != null)
                    {
                        foreach (var kvp in snapshot.OtherRiderLocations)
                            this.OtherRiderLocations[kvp.Key] = kvp.Value;
                    }

                    // 2. Zero out ephemeral / in-flight state
                    this.OtherRiderSpeeds.Clear();
                    this.RunningInBackground = false;
                    this.LastStopTime = null;
                    this.StopStartTime = null;
                    this.OffRouteStrikeCount = 0;

                    // Reset network & alert cooldowns so rider gets immediate alerts
                    this.LastSpeedAlert = DateTime.MinValue;
                    this.LastArrivalAlert = DateTime.MinValue;
                    this.LastSplinterAlert = DateTime.MinValue;
                    this.LastLagAlert = DateTime.MinValue;
                    this.LastBroadcastLocation = null;
                    this.LastTrafficAlertTime = DateTime.MinValue;
                    this.LastTrafficRefreshTime = DateTime.MinValue;
                    this.LastTrafficRefreshRouteIndex = snapshot.CurrentRouteIndex;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Snapshot Error] Failed to load state: {ex.Message}");
            }
        }

        public void ClearSnapshot()
        {
            try
            {
                if (File.Exists(SnapshotFilePath)) File.Delete(SnapshotFilePath);
                var tempFile = SnapshotFilePath + ".tmp";
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch { }
        }

        // ==========================================
        // RESET HANDLERS
        // ==========================================
        public void HardResetAll()
        {
            ResetNavigationState();
            ResetTelemetryState();
            ClearSnapshot();
        }

        public void ResetNavigationState()
        {
            ActiveDestination = null;
            ActiveDestinationName = string.Empty;
            ActiveMeetupPoint = null;
            CurrentRoutePoints.Clear();
            LastRerouteTime = DateTime.MinValue;
            CurrentRouteIndex = 0;
            CurrentTrafficData.Clear();
            CachedMainRouteData = null;
            ActiveWaypoints.Clear();
        }

        public void ResetTelemetryState()
        {
            CumulativeDistanceKm = 0;
            LastOdometerLocation = null;
            MaxSpeedKmh = 0;
            TopSpeedKmh = 0;
            TotalStoppedTime = TimeSpan.Zero;
            LastStopTime = null;
            StopStartTime = null;
            RideStartTime = DateTime.UtcNow;

            LastSpeedAlert = DateTime.MinValue;
            LastArrivalAlert = DateTime.MinValue;
            LastSplinterAlert = DateTime.MinValue;
            LastLagAlert = DateTime.MinValue;
            LastGroupPitstopKm = 0;
            OffRouteStrikeCount = 0;
            LastAnnouncedElevation = null;

            LastTrafficAlertTime = DateTime.MinValue;
            LastTrafficRefreshTime = DateTime.MinValue;
            LastTrafficRefreshRouteIndex = 0;
            // --- VISIBILITY & NETWORK STATE ---
            HiddenRiders = new();
            UsersWhoMutedMe = new();
            VisibilityInitialized = new();

            // --- NAVIGATION FLAGS ---
            HaveIReachedMeetup = false;
            HasAnnouncedArrival = false;
            LastAnnouncedTurn = null;
            RidersAtMeetup = new();
            PendingCatchUpState = null;

            // --- SENSOR & TIMING COOLDOWNS ---
            LastCrashEvent = DateTime.MinValue;
            LastNetworkBroadcastTime = DateTime.MinValue;

        }
    }
    // ==========================================
    // SNAPSHOT DTO
    // ==========================================
    public class RideSnapshotDto
    {
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

        // Time & Metrics Continuity
        public DateTime RideStartTime { get; set; }
        public double TotalStoppedTimeSeconds { get; set; }
        public double LastGroupPitstopKm { get; set; }
        public double? LastAnnouncedElevation { get; set; }

        // Traffic state
        public List<SpeedInterval> CurrentTrafficData { get; set; }

        public Dictionary<string, Location> OtherRiderLocations { get; set; }
    }
}