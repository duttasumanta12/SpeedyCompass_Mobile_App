namespace SpeedyCompass.Backend.Hubs;

public class RiderInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = false;
    public bool IsOnline { get; internal set; }
    public string ConnectionId { get; set; } = string.Empty;
    // NEW: Role exposed to the frontend roster
    public string Role { get; set; } = "Rider";
    public string GoogleId { get; internal set; }
}
