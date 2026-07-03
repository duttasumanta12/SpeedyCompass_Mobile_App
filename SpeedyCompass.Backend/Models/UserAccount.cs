namespace SpeedyCompass.Backend.Hubs;

public class UserAccount
{
    public string GoogleId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    // We now store the active SignalR connection ID here
    public string ConnectionId { get; set; } = string.Empty;
}
