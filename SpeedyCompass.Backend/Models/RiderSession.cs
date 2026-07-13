namespace SpeedyCompass.Backend.Hubs;

public class RiderSession
{
    public string ConnectionId { get; set; } = string.Empty; // Added for List compatibility
    public string UserName { get; set; } = string.Empty;
    public string GoogleId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    // NEW: Caching latest location in RAM for instant math
    public double LastLat { get; set; }
    public double LastLng { get; set; }
    public DateTime LastUpdate { get; set; }
}
