using MongoDB.Bson.Serialization.Attributes;

namespace SpeedyCompass.Backend.Hubs;

public class GroupSession
{
    [BsonId]
    public string GroupName { get; set; } = string.Empty; // Added for List compatibility
    public string AdminConnectionId { get; set; } = string.Empty;
    public string AdminGoogleId { get; set; } = string.Empty;
    public bool IsNavigating { get; set; } = false;
    public double DestLat { get; set; }
    public double DestLng { get; set; }
    public string DestName { get; set; } = string.Empty;
    // --- NEW: Tracks who holds the microphone ---
    public string ActiveSpeaker { get; set; } = string.Empty;
    public GroupSettings Settings { get; set; } = new GroupSettings();
}
// --- NEW: THE SETTINGS SCHEMA ---
public class GroupSettings
{
    public int MaxLagDistanceMeters { get; set; } = 500;
    public int SplinterWarningDistanceMeters { get; set; } = 2000;
    public int ArrivalGeofenceMeters { get; set; } = 1000;
    public string LeadRiderGoogleId { get; set; } = string.Empty;
    public string SweepRiderGoogleId { get; set; } = string.Empty;
}
