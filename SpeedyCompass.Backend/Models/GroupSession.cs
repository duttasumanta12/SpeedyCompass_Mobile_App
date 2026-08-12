using MongoDB.Bson.Serialization.Attributes;
using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass.Backend.Hubs;

public class GroupSession
{
    [BsonId]
    public string GroupName { get; set; } = string.Empty; // Added for List compatibility
    public string AdminConnectionId { get; set; } = string.Empty;
    public string AdminGoogleId { get; set; } = string.Empty;
    // --- THE FIX: Replaced bool IsNavigating with the Enum ---
    public GroupState CurrentState { get; set; } = GroupState.NotNavigating;
    public double DestLat { get; set; }
    public double DestLng { get; set; }
    public string DestName { get; set; } = string.Empty;
    // --- NEW: Tracks who holds the microphone ---
    public string ActiveSpeaker { get; set; } = string.Empty;
    public GroupSettings Settings { get; set; } = new GroupSettings();
    public string JoinCode { get; internal set; }
    public double MeetupLat { get; set; }
    public double MeetupLng { get; set; }
    public bool IsMeetupActive { get; set; }
    public int StateVersion { get; set; } = 1;
}
// --- NEW: THE SETTINGS SCHEMA ---
public class GroupSettings
{
    public int MaxLagDistanceMeters { get; set; } = 500;
    public int SplinterWarningDistanceMeters { get; set; } = 2000;
    public int ArrivalGeofenceMeters { get; set; } = 1000;
    public string LeadRiderGoogleId { get; set; } = string.Empty;
    public string SweepRiderGoogleId { get; set; } = string.Empty;
    // NEW: Configurable Max Group Size (Default to 15, max 20)
    public int MaxGroupSize { get; set; } = 15;
    // NEW: Configurable Pitstop Reminder Distance (Default 100km)
    public int PitstopDistanceMeters { get; set; } = 100000;
    public bool EnableDynamicRouting { get; set; } = true;
    public int MinUpdateDistanceMeters { get; internal set; }
    public int MaxUpdateDistanceMeters { get; internal set; }
    public int DeviationSensitivityMeters { get; internal set; }
    public ConvoySyncProtocol ConvoyUpdateProtocol { get; internal set; }
}
