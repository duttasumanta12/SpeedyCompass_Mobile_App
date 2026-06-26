using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace SpeedyCompass.Backend.Hubs;

// Simple state tracking models
public class GroupSession
{
    public string AdminConnectionId { get; set; } = string.Empty;
    public bool IsNavigating { get; set; } = false;
}

public class RiderSession
{
    public string UserName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
}

public class RiderInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = false;
}

public class CompassHub : Hub
{
    // Thread-safe dictionaries to hold state in-memory.
    // Note: If you scale to multiple ASP.NET server instances, this state 
    // should be moved to Redis cache instead of static memory.
    private static readonly ConcurrentDictionary<string, GroupSession> _activeGroups = new();
    private static readonly ConcurrentDictionary<string, RiderSession> _connectedRiders = new();

    /// <summary>
    /// Helper to get the current list of all riders in a specific group
    /// </summary>
    private List<RiderInfo> GetGroupRoster(string groupName)
    {
        var roster = new List<RiderInfo>();
        if (_activeGroups.TryGetValue(groupName, out var session))
        {
            var ridersInGroup = _connectedRiders.Where(r => r.Value.GroupName == groupName).ToList();
            foreach (var rider in ridersInGroup)
            {
                roster.Add(new RiderInfo
                {
                    Name = rider.Value.UserName,
                    IsAdmin = rider.Key == session.AdminConnectionId
                });
            }
        }
        return roster;
    }

    /// <summary>
    /// Checks if a group name is already taken.
    /// </summary>
    public bool CheckGroupExists(string groupName)
    {
        return _activeGroups.ContainsKey(groupName);
    }

    /// <summary>
    /// Creates a new group and assigns the caller as the Admin.
    /// </summary>
    public async Task CreateGroup(string groupName, string userName)
    {
        var session = new GroupSession { AdminConnectionId = Context.ConnectionId };

        if (_activeGroups.TryAdd(groupName, session))
        {
            _connectedRiders.TryAdd(Context.ConnectionId, new RiderSession { UserName = userName, GroupName = groupName });

            // Add user to the SignalR group primitive
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

            // Broadcast the initial roster to the admin
            await Clients.Group(groupName).SendAsync("RosterUpdated", GetGroupRoster(groupName));
        }
        else
        {
            throw new HubException("Group already exists.");
        }
    }

    /// <summary>
    /// Joins an existing group and alerts current members.
    /// </summary>
    public async Task JoinGroup(string groupName, string userName)
    {
        if (_activeGroups.TryGetValue(groupName, out var session))
        {
            _connectedRiders.TryAdd(Context.ConnectionId, new RiderSession { UserName = userName, GroupName = groupName });
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

            // Broadcast the full updated roster to EVERYONE in the group, including the new joiner
            await Clients.Group(groupName).SendAsync("RosterUpdated", GetGroupRoster(groupName));
        }
        else
        {
            throw new HubException("Group not found.");
        }
    }

    /// <summary>
    /// Admin calls this to start the navigation for everyone.
    /// </summary>
    public async Task StartNavigation(string groupName, double destLat, double destLng, string destName)
    {
        if (_activeGroups.TryGetValue(groupName, out var session))
        {
            // Security check: Only the Admin can start navigation
            if (session.AdminConnectionId != Context.ConnectionId)
            {
                throw new HubException("Only the Admin can start navigation.");
            }

            session.IsNavigating = true;

            // Broadcast to the entire group to switch screens
            await Clients.Group(groupName).SendAsync("NavigationStarted", destLat, destLng, destName);
        }
    }

    /// <summary>
    /// The high-frequency telemetry payload.
    /// </summary>
    public async Task UpdateMyLocation(string groupName, double lat, double lng, double heading)
    {
        if (_connectedRiders.TryGetValue(Context.ConnectionId, out var rider))
        {
            // Send coordinates to everyone in the group EXCEPT the sender
            await Clients.GroupExcept(groupName, Context.ConnectionId)
                         .SendAsync("ReceiveRiderLocation", rider.UserName, lat, lng, heading);
        }
    }

    /// <summary>
    /// Handle unexpected disconnects (like a user driving through a tunnel and losing cell service).
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectedRiders.TryRemove(Context.ConnectionId, out var rider))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, rider.GroupName);

            // Tell the group that the user has dropped offline
            await Clients.Group(rider.GroupName).SendAsync("UserLeft", rider.UserName);

            // Cleanup: If the admin leaves or group is empty, handle group deletion
            if (_activeGroups.TryGetValue(rider.GroupName, out var session))
            {
                if (session.AdminConnectionId == Context.ConnectionId)
                {
                    await Clients.Group(rider.GroupName).SendAsync("AdminDisconnected");
                    _activeGroups.TryRemove(rider.GroupName, out _);
                }
                else
                {
                    // If a regular rider leaves, update the roster for the remaining users
                    await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", GetGroupRoster(rider.GroupName));
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}