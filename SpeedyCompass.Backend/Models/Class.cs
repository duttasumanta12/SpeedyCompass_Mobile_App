using System.Collections.Generic;

namespace SpeedyCompass.Backend.Hubs;

public class UserAccount
{
    public string GoogleId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    // We now store the active SignalR connection ID here
    public string ConnectionId { get; set; } = string.Empty;
}

public class GroupSession
{
    public string GroupName { get; set; } = string.Empty; // Added for List compatibility
    public string AdminConnectionId { get; set; } = string.Empty;
    public string AdminGoogleId { get; set; } = string.Empty;
    public bool IsNavigating { get; set; } = false;
    public double DestLat { get; set; }
    public double DestLng { get; set; }
    public string DestName { get; set; } = string.Empty;
}

public class RiderSession
{
    public string ConnectionId { get; set; } = string.Empty; // Added for List compatibility
    public string UserName { get; set; } = string.Empty;
    public string GoogleId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
}

public class ActiveGroupDto
{
    public string GroupName { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public string AdminGoogleId { get; set; } = string.Empty;
    public bool IsNavigating { get; set; }
}

public class RiderInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = false;
    public bool IsOnline { get; internal set; }
}

// The Singleton Service
public class CompassStateManager
{
    public List<GroupSession> ActiveGroups { get; } = new();
    public List<RiderSession> ConnectedRiders { get; } = new();

    // Global User Registry
    public List<UserAccount> UserAccounts { get; } = new();
    public List<string> TakenUsernames { get; } = new();
}