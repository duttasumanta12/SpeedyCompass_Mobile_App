using Microsoft.AspNetCore.SignalR;

namespace SpeedyCompass.Backend.Hubs;

public class CompassHub : Hub
{
    private readonly CompassStateManager _state;

    public CompassHub(CompassStateManager state)
    {
        _state = state;
    }

    // FIX: Changed from private to public so the Lobby can sync manually!
    public List<RiderInfo> GetGroupRoster(string groupName)
    {
        var roster = new List<RiderInfo>();
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);

        if (session != null)
        {
            var ridersInGroup = _state.ConnectedRiders.Where(r => r.GroupName == groupName).ToList();
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

    public string AuthenticateUser(string googleId)
    {
        var account = _state.UserAccounts.FirstOrDefault(u => u.GoogleId == googleId);
        if (account != null)
        {
            account.ConnectionId = Context.ConnectionId;
            return account.Username;
        }
        return string.Empty;
    }

    public string RegisterOrUpdateUser(string currentGoogleId, string desiredUsername)
    {
        var owner = _state.UserAccounts.FirstOrDefault(u => u.Username.Equals(desiredUsername, StringComparison.OrdinalIgnoreCase));
        if (owner != null)
        {
            if (string.IsNullOrEmpty(currentGoogleId) || owner.GoogleId != currentGoogleId)
                throw new HubException($"The username '{desiredUsername}' is already taken.");
        }

        string googleIdToUse = string.IsNullOrEmpty(currentGoogleId) ? Guid.NewGuid().ToString() : currentGoogleId;
        var existingAccount = _state.UserAccounts.FirstOrDefault(u => u.GoogleId == googleIdToUse);

        if (existingAccount != null)
        {
            existingAccount.Username = desiredUsername;
            existingAccount.ConnectionId = Context.ConnectionId;
        }
        else
        {
            var newAccount = new UserAccount { GoogleId = googleIdToUse, Username = desiredUsername, ConnectionId = Context.ConnectionId };
            _state.UserAccounts.Add(newAccount);
        }

        if (!_state.TakenUsernames.Contains(desiredUsername)) _state.TakenUsernames.Add(desiredUsername);
        return googleIdToUse;
    }

    // --- MAIN PAGE DISCOVERY METHODS ---
    public async Task<List<ActiveGroupDto>> GetActiveGroups()
    {
        var list = new List<ActiveGroupDto>();
        foreach (var session in _state.ActiveGroups)
        {
            list.Add(new ActiveGroupDto
            {
                GroupName = session.GroupName,
                AdminGoogleId = session.AdminGoogleId,
                MemberCount = _state.ConnectedRiders.Count(r => r.GroupName == session.GroupName),
                IsNavigating = session.IsNavigating
            });
        }
        return list;
    }

    public async Task CreateGroup(string groupName, string userName, string googleId)
    {
        if (_state.ConnectedRiders.Any(r => r.GoogleId == googleId))
            throw new HubException("You are already in a group. Please leave it first.");

        var acc = _state.UserAccounts.FirstOrDefault(u => u.GoogleId == googleId);
        if (acc != null) acc.ConnectionId = Context.ConnectionId;

        if (_state.ActiveGroups.Any(g => g.GroupName == groupName))
            throw new HubException("Group already exists.");

        var session = new GroupSession { GroupName = groupName, AdminConnectionId = Context.ConnectionId, AdminGoogleId = googleId };
        _state.ActiveGroups.Add(session);
        _state.ConnectedRiders.Add(new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName });

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", GetGroupRoster(groupName));
    }

    public async Task JoinGroup(string groupName, string userName, string googleId)
    {
        var acc = _state.UserAccounts.FirstOrDefault(u => u.GoogleId == googleId);
        if (acc != null) acc.ConnectionId = Context.ConnectionId;

        var existingRider = _state.ConnectedRiders.FirstOrDefault(r => r.GoogleId == googleId);
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);

        if (session != null)
        {
            if (existingRider != null)
            {
                if (existingRider.GroupName != groupName)
                {
                    if (!string.IsNullOrEmpty(existingRider.ConnectionId))
                        await Groups.RemoveFromGroupAsync(existingRider.ConnectionId, existingRider.GroupName);

                    _state.ConnectedRiders.Remove(existingRider);
                    _state.ConnectedRiders.Add(new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName });
                    await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
                }
                else
                {
                    existingRider.ConnectionId = Context.ConnectionId;
                    await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
                    if (session.AdminGoogleId == googleId) session.AdminConnectionId = Context.ConnectionId;
                }
            }
            else
            {
                if (GetGroupRoster(groupName).Count >= 5) throw new HubException("Group is full (Max 5 members).");
                _state.ConnectedRiders.Add(new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName });
                await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            }

            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("UserJoinedAlert", userName);
            await Clients.Group(groupName).SendAsync("RosterUpdated", GetGroupRoster(groupName));

            if (session.IsNavigating)
                await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName);
            else if (!string.IsNullOrEmpty(session.DestName))
                await Clients.Caller.SendAsync("DestinationSet", session.DestLat, session.DestLng, session.DestName);
        }
        else throw new HubException("Group not found.");
    }

    // --- LOBBY VS GROUP LIFECYCLE ---
    public async Task LeaveLobby()
    {
        var rider = _state.ConnectedRiders.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
        {
            rider.ConnectionId = string.Empty;
            await Clients.Group(rider.GroupName).SendAsync("UserOfflineAlert", rider.UserName);
            await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", GetGroupRoster(rider.GroupName));

            var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == rider.GroupName);
            if (session != null)
            {
                // NEW: Release PTT lock if the active speaker goes offline
                if (session.ActiveSpeaker == rider.UserName)
                {
                    session.ActiveSpeaker = string.Empty;
                    await Clients.Group(rider.GroupName).SendAsync("PttReleased");
                }

                if (session.AdminGoogleId == rider.GoogleId && session.IsNavigating)
                {
                    session.IsNavigating = false;
                    await Clients.Group(rider.GroupName).SendAsync("NavigationCancelled");
                }
            }
        }
    }

    public async Task LeaveGroup()
    {
        var rider = _state.ConnectedRiders.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
        {
            _state.ConnectedRiders.Remove(rider);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, rider.GroupName);
            var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == rider.GroupName);

            if (session != null && session.AdminGoogleId == rider.GoogleId)
            {
                await Clients.Group(rider.GroupName).SendAsync("GroupDeleted");
                _state.ActiveGroups.Remove(session);
                _state.ConnectedRiders.RemoveAll(r => r.GroupName == rider.GroupName);
            }
            else
            {
                await Clients.Group(rider.GroupName).SendAsync("UserLeftAlert", rider.UserName);
                await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", GetGroupRoster(rider.GroupName));
            }
        }
    }

    public async Task DeleteGroup(string groupName)
    {
        var currentUser = _state.UserAccounts.FirstOrDefault(u => u.ConnectionId == Context.ConnectionId);
        if (currentUser == null) throw new HubException("Unauthenticated request.");

        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);
        if (session != null && session.AdminGoogleId == currentUser.GoogleId)
        {
            await Clients.Group(groupName).SendAsync("GroupDeleted");
            _state.ActiveGroups.Remove(session);
            _state.ConnectedRiders.RemoveAll(r => r.GroupName == groupName);
        }
        else throw new HubException("Only the group admin can delete this group.");
    }

    // --- NAVIGATION LOGIC ---
    public async Task SetDestination(string groupName, double destLat, double destLng, string destName)
    {
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);
        if (session != null && session.AdminConnectionId == Context.ConnectionId)
        {
            session.DestLat = destLat; session.DestLng = destLng; session.DestName = destName;
            await Clients.Group(groupName).SendAsync("DestinationSet", destLat, destLng, destName);
        }
    }

    public async Task StartNavigation(string groupName, double destLat, double destLng, string destName)
    {
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);
        if (session != null && session.AdminConnectionId == Context.ConnectionId)
        {
            session.IsNavigating = true;
            session.DestLat = destLat; session.DestLng = destLng; session.DestName = destName;
            await Clients.Group(groupName).SendAsync("NavigationStarted", destLat, destLng, destName);
        }
    }

    public async Task CancelNavigation(string groupName)
    {
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);
        if (session != null && session.AdminConnectionId == Context.ConnectionId)
        {
            session.IsNavigating = false;
            await Clients.Group(groupName).SendAsync("NavigationCancelled");
        }
    }

    public async Task SendGroupAlert(string groupName, string alertType, string senderName)
    {
        if (_state.ActiveGroups.Any(g => g.GroupName == groupName))
            await Clients.Group(groupName).SendAsync("ReceiveAlert", alertType, senderName);
    }

    public async Task UpdateMyLocation(string groupName, double lat, double lng, double heading)
    {
        var rider = _state.ConnectedRiders.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveRiderLocation", rider.UserName, lat, lng, heading);
    }

    // --- NEW: PUSH TO TALK (PTT) LOGIC ---
    public async Task RequestPtt(string groupName, string userName)
    {
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);
        if (session != null)
        {
            if (string.IsNullOrEmpty(session.ActiveSpeaker))
            {
                // Channel is free. Lock it and notify everyone.
                session.ActiveSpeaker = userName;
                await Clients.Group(groupName).SendAsync("PttLocked", userName);
            }
            else
            {
                // Channel is busy. Tell the requester they are denied.
                await Clients.Caller.SendAsync("PttDenied", session.ActiveSpeaker);
            }
        }
    }

    public async Task ReleasePtt(string groupName, string userName)
    {
        var session = _state.ActiveGroups.FirstOrDefault(g => g.GroupName == groupName);
        if (session != null && session.ActiveSpeaker == userName)
        {
            // Only the person holding the mic can release it
            session.ActiveSpeaker = string.Empty;
            await Clients.Group(groupName).SendAsync("PttReleased");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveLobby();
        await base.OnDisconnectedAsync(exception);
    }
}