namespace SpeedyCompass.Backend.Hubs;

public class RiderSession
{
    public string ConnectionId { get; set; } = string.Empty; // Added for List compatibility
    public string UserName { get; set; } = string.Empty;
    public string GoogleId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
}
