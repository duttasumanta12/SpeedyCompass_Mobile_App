using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;

namespace SpeedyCompass.Backend.Hubs;

public class CompassHub : Hub
{
    private readonly CompassStateManager _state;

    public CompassHub(CompassStateManager state)
    {
        _state = state;
    }

    // FIX: Changed from private to public so the Lobby can sync manually!
    public async Task<List<RiderInfo>> GetGroupRoster(string groupName)
    {
        var roster = new List<RiderInfo>();
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (session != null)
        {
            var ridersInGroup = _state.ConnectedRiders.Values.Where(r => r.GroupName == groupName).ToList();
            foreach (var rider in ridersInGroup)
            {
                roster.Add(new RiderInfo
                {
                    Name = rider.UserName,
                    IsAdmin = rider.GoogleId == session.AdminGoogleId,
                    IsOnline = !string.IsNullOrEmpty(rider.ConnectionId)
                });
            }
        }
        return roster;
    }

    // --- AUTHENTICATION & USER REGISTRY ---

    // --- AUTHENTICATION & USER REGISTRY ---
    public async Task<string?> AuthenticateUser(string googleId)
    {
        var account = await _state.UserAccounts.Find(u => u.GoogleId == googleId).FirstOrDefaultAsync();
        return account?.Username ?? string.Empty;
    }

    public async Task<string> RegisterOrUpdateUser(string currentGoogleId, string desiredUsername)
    {
        var owner = await _state.UserAccounts.Find(u => u.Username.ToLower() == desiredUsername.ToLower()).FirstOrDefaultAsync();
        if (owner != null && (string.IsNullOrEmpty(currentGoogleId) || owner.GoogleId != currentGoogleId))
        {
            throw new HubException($"The username '{desiredUsername}' is already taken.");
        }

        string googleIdToUse = string.IsNullOrEmpty(currentGoogleId) ? Guid.NewGuid().ToString() : currentGoogleId;
        var newAccount = new UserAccount { GoogleId = googleIdToUse, Username = desiredUsername };

        // MongoDB Upsert
        await _state.UserAccounts.ReplaceOneAsync(u => u.GoogleId == googleIdToUse, newAccount, new ReplaceOptions { IsUpsert = true });
        return googleIdToUse;
    }

    // --- MAIN PAGE DISCOVERY METHODS ---
    // --- MAIN PAGE DISCOVERY METHODS ---
    public async Task<List<ActiveGroupDto>> GetActiveGroups()
    {
        var list = new List<ActiveGroupDto>();
        var allGroups = await _state.ActiveGroups.Find(_ => true).ToListAsync();

        foreach (var session in allGroups)
        {
            list.Add(new ActiveGroupDto
            {
                GroupName = session.GroupName,
                AdminGoogleId = session.AdminGoogleId,
                MemberCount = _state.ConnectedRiders.Values.Count(r => r.GroupName == session.GroupName),
                IsNavigating = session.IsNavigating
            });
        }
        return list;
    }

    public async Task CreateGroup(string groupName, string userName, string googleId)
    {
        if (_state.ConnectedRiders.Values.Any(r => r.GoogleId == googleId))
            throw new HubException("You are already in a group. Please leave it first.");

        var existingGroup = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (existingGroup != null) throw new HubException("Group already exists.");

        var session = new GroupSession { GroupName = groupName, AdminGoogleId = googleId };

        await _state.ActiveGroups.InsertOneAsync(session);
        _state.ConnectedRiders.TryAdd(Context.ConnectionId, new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName });

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));
    }

    public async Task JoinGroup(string groupName, string userName, string googleId)
    {
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session == null) throw new HubException("Group not found.");

        var roster = await GetGroupRoster(groupName);
        if (roster.Count >= 5 && roster.All(r => r.ConnectionId != Context.ConnectionId))
            throw new HubException("Group is full (Max 5 members).");

        _state.ConnectedRiders.TryAdd(Context.ConnectionId, new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName });
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("UserJoinedAlert", userName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));

        if (session.IsNavigating)
            await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName);
        else if (!string.IsNullOrEmpty(session.DestName))
            await Clients.Caller.SendAsync("DestinationSet", session.DestLat, session.DestLng, session.DestName);
    }

    // --- LOBBY VS GROUP LIFECYCLE ---
    public async Task LeaveLobby()
    {
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var rider);
        if (rider != null)
        {
            rider.ConnectionId = string.Empty; // Mark Offline in RAM
            await Clients.Group(rider.GroupName).SendAsync("UserOfflineAlert", rider.UserName);
            await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", await GetGroupRoster(rider.GroupName));

            var session = await _state.ActiveGroups.Find(g => g.GroupName == rider.GroupName).FirstOrDefaultAsync();
            if (session != null)
            {
                if (_state.ActiveSpeakers.TryGetValue(rider.GroupName, out var activeSpeaker) && activeSpeaker == rider.UserName)
                {
                    _state.ActiveSpeakers.TryRemove(rider.GroupName, out _);
                    await Clients.Group(rider.GroupName).SendAsync("PttReleased");
                }

                if (session.AdminGoogleId == rider.GoogleId && session.IsNavigating)
                {
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == rider.GroupName, Builders<GroupSession>.Update.Set(g => g.IsNavigating, false));
                    await Clients.Group(rider.GroupName).SendAsync("NavigationCancelled");
                }
            }
        }
    }

    public async Task LeaveGroup()
    {
        if (_state.ConnectedRiders.TryRemove(Context.ConnectionId, out var rider))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, rider.GroupName);
            var session = await _state.ActiveGroups.Find(g => g.GroupName == rider.GroupName).FirstOrDefaultAsync();

            if (session != null && session.AdminGoogleId == rider.GoogleId)
            {
                await Clients.Group(rider.GroupName).SendAsync("GroupDeleted");
                await _state.ActiveGroups.DeleteOneAsync(g => g.GroupName == rider.GroupName);

                var usersToRemove = _state.ConnectedRiders.Where(r => r.Value.GroupName == rider.GroupName).Select(r => r.Key).ToList();
                foreach (var id in usersToRemove) _state.ConnectedRiders.TryRemove(id, out _);
            }
            else
            {
                await Clients.Group(rider.GroupName).SendAsync("UserLeftAlert", rider.UserName);
                await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", await GetGroupRoster(rider.GroupName));
            }
        }
    }

    public async Task DeleteGroup(string groupName)
    {
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var caller);
        if (caller == null) throw new HubException("Unauthenticated request.");

        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session != null && session.AdminGoogleId == caller.GoogleId)
        {
            await Clients.Group(groupName).SendAsync("GroupDeleted");
            await _state.ActiveGroups.DeleteOneAsync(g => g.GroupName == groupName);

            var usersToRemove = _state.ConnectedRiders.Where(r => r.Value.GroupName == groupName).Select(r => r.Key).ToList();
            foreach (var id in usersToRemove) _state.ConnectedRiders.TryRemove(id, out _);
        }
        else throw new HubException("Only the group admin can delete this group.");
    }

    // --- NAVIGATION LOGIC ---
    public async Task SetDestination(string groupName, double destLat, double destLng, string destName)
    {
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var caller);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            var update = Builders<GroupSession>.Update.Set(g => g.DestLat, destLat).Set(g => g.DestLng, destLng).Set(g => g.DestName, destName);
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.Group(groupName).SendAsync("DestinationSet", destLat, destLng, destName);
        }
    }

    public async Task StartNavigation(string groupName, double destLat, double destLng, string destName)
    {
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var caller);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            var update = Builders<GroupSession>.Update.Set(g => g.IsNavigating, true).Set(g => g.DestLat, destLat).Set(g => g.DestLng, destLng).Set(g => g.DestName, destName);
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.Group(groupName).SendAsync("NavigationStarted", destLat, destLng, destName);
        }
    }

    public async Task CancelNavigation(string groupName)
    {
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var caller);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.IsNavigating, false));
            await Clients.Group(groupName).SendAsync("NavigationCancelled");
        }
    }

    public async Task SendGroupAlert(string groupName, string alertType, string senderName)
    {
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session != null) await Clients.Group(groupName).SendAsync("ReceiveAlert", alertType, senderName);
    }

    // --- UPGRADED: TELEMETRY ENGINE ---
    public async Task UpdateMyLocation(string groupName, string userName, double lat, double lng, double heading)
    {
        // 1. Broadcast the movement visually to the map
        await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveRiderLocation", userName, lat, lng, heading);

        // 2. Fetch memory state
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var currentRider);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (session != null && currentRider != null)
        {
            currentRider.LastLat = lat;
            currentRider.LastLng = lng;

            // Simplified Math: Assume the Admin is the Lead Rider
            string leadGoogleId = session.AdminGoogleId;

            if (currentRider.GoogleId != leadGoogleId)
            {
                // This is a normal rider. Check if they are falling behind the Lead.
                var leadRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == leadGoogleId);
                if (leadRider != null && leadRider.LastLat != 0)
                {
                    double distance = CalculateDistanceMeters(lat, lng, leadRider.LastLat, leadRider.LastLng);
                    if (distance > session.Settings.MaxLagDistanceMeters)
                    {
                        // Alert the Admin
                        var adminConn = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == session.AdminGoogleId)?.ConnectionId;
                        if (!string.IsNullOrEmpty(adminConn))
                        {
                            await Clients.Client(adminConn).SendAsync("ReceiveAlert", "Lagging", $"{userName} is {Math.Round(distance)} meters behind.");
                        }
                    }
                }
            }
            else
            {
                // This IS the Lead Rider moving. Check if the convoy has splintered (anyone too far back).
                double maxDist = 0;
                foreach (var r in _state.ConnectedRiders.Values.Where(x => x.GroupName == groupName && x.GoogleId != leadGoogleId))
                {
                    if (r.LastLat != 0)
                    {
                        double d = CalculateDistanceMeters(lat, lng, r.LastLat, r.LastLng);
                        if (d > maxDist) maxDist = d;
                    }
                }

                if (maxDist > session.Settings.SplinterWarningDistanceMeters)
                {
                    await Clients.Client(currentRider.ConnectionId).SendAsync("ReceiveAlert", "Splinter", "Convoy splintered! A rider has fallen too far behind.");
                }
            }
        }
    }

    // --- NEW: PUSH TO TALK (PTT) LOGIC ---
    public async Task RequestPtt(string groupName, string userName)
    {
        if (!_state.ActiveSpeakers.ContainsKey(groupName))
        {
            // TryAdd prevents race conditions if two people press the button at the exact same millisecond
            if (_state.ActiveSpeakers.TryAdd(groupName, userName))
            {
                await Clients.Group(groupName).SendAsync("PttLocked", userName);
                return;
            }
        }

        _state.ActiveSpeakers.TryGetValue(groupName, out var activeSpeaker);
        await Clients.Caller.SendAsync("PttDenied", activeSpeaker);
    }

    public async Task ReleasePtt(string groupName, string userName)
    {
        if (_state.ActiveSpeakers.TryGetValue(groupName, out var currentSpeaker) && currentSpeaker == userName)
        {
            _state.ActiveSpeakers.TryRemove(groupName, out _);
            await Clients.Group(groupName).SendAsync("PttReleased");
        }
    }
    // --- CONNECTION RESTORATION LOGIC ---
    public async Task RestoreConnectionState(string googleId, string userName, string groupName)
    {
        var newConnectionId = Context.ConnectionId;
        if (string.IsNullOrEmpty(groupName)) return;

        // Verify the group still exists in Cosmos DB
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        var existingEntry = _state.ConnectedRiders.FirstOrDefault(x => x.Value.GoogleId == googleId && x.Value.GroupName == groupName);
        if (existingEntry.Key != null)
        {
            _state.ConnectedRiders.TryRemove(existingEntry.Key, out _);
        }

        _state.ConnectedRiders.TryAdd(newConnectionId, new RiderSession { ConnectionId = newConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName });

        await Groups.AddToGroupAsync(newConnectionId, groupName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));

        if (session != null)
        {
            if (session.IsNavigating)
                await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName);
            else if (!string.IsNullOrEmpty(session.DestName))
                await Clients.Caller.SendAsync("DestinationSet", session.DestLat, session.DestLng, session.DestName);
        }
    }
    public async Task UpdateGroupSettings(string groupName, int maxLag, int splinterDist)
    {
        _state.ConnectedRiders.TryGetValue(Context.ConnectionId, out var caller);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            var update = Builders<GroupSession>.Update
                .Set(g => g.Settings.MaxLagDistanceMeters, maxLag)
                .Set(g => g.Settings.SplinterWarningDistanceMeters, splinterDist);

            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveLobby();
        await base.OnDisconnectedAsync(exception);
    }
    private double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var earthRadiusMeters = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMeters * c;
    }
}