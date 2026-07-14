using MongoDB.Bson.Serialization.Attributes;

namespace SpeedyCompass.Backend.Hubs;

public class UserAccount
{
    [BsonId]
    public string GoogleId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    // We now store the active SignalR connection ID here
    public string ConnectionId { get; set; } = string.Empty;
    // NEW: Personal Profile & Privacy Fields
    public string EmergencyContact { get; set; }
    public string VehicleNumber { get; set; }
    public string BloodGroup { get; set; }
    public bool HasConsented { get; set; }
}
