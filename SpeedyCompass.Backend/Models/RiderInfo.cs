namespace SpeedyCompass.Backend.Hubs;

public class RiderInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = false;
    public bool IsOnline { get; internal set; }
}
