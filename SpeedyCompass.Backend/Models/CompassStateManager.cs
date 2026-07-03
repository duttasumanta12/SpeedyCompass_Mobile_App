using System.Collections.Generic;

namespace SpeedyCompass.Backend.Hubs;

// The Singleton Service
public class CompassStateManager
{
    public List<GroupSession> ActiveGroups { get; } = new();
    public List<RiderSession> ConnectedRiders { get; } = new();

    // Global User Registry
    public List<UserAccount> UserAccounts { get; } = new();
    public List<string> TakenUsernames { get; } = new();
}