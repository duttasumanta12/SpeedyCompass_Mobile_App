using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpeedyCompass.Backend.Hubs;

public class CompassHub : Hub
{
    private readonly CompassStateManager _state;
    // NEW: Tracks the exact time an alert was fired so we don't spam the Admin!
    private static readonly ConcurrentDictionary<string, DateTime> _alertCooldowns = new();

    public CompassHub(CompassStateManager state)
    {
        _state = state;
    }

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
                    GoogleId = rider.GoogleId,
                    IsAdmin = rider.GoogleId == session.AdminGoogleId,
                    IsOnline = !string.IsNullOrEmpty(rider.ConnectionId),
                    Role = rider.Role // Ensure role is synced to UI
                });
            }
        }
        return roster;
    }

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

        await _state.UserAccounts.ReplaceOneAsync(u => u.GoogleId == googleIdToUse, newAccount, new ReplaceOptions { IsUpsert = true });
        return googleIdToUse;
    }

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
                IsNavigating = session.IsNavigating,
                MaxGroupSize = session.Settings.MaxGroupSize
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

        var session = new GroupSession
        {
            GroupName = groupName,
            AdminGoogleId = googleId,
            Settings = new GroupSettings
            {
                MaxGroupSize = 15,
                MaxLagDistanceMeters = 500,
                SplinterWarningDistanceMeters = 2000,
                ArrivalGeofenceMeters = 1000,
                LeadRiderGoogleId = googleId
            }
        };

        await _state.ActiveGroups.InsertOneAsync(session);

        // FIX: Key the dictionary by GoogleId!
        _state.ConnectedRiders[googleId] = new RiderSession
        {
            ConnectionId = Context.ConnectionId,
            UserName = userName,
            GoogleId = googleId,
            GroupName = groupName,
            Role = "Lead"
        };

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));
    }

    public async Task JoinGroup(string groupName, string userName, string googleId)
    {
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session == null) throw new HubException("Group not found.");

        int currentMembers = _state.ConnectedRiders.Values.Count(r => r.GroupName == groupName);

        if (currentMembers >= session.Settings.MaxGroupSize && session.AdminGoogleId != googleId)
            throw new HubException("Group is full.");

        // FIX: Key the dictionary by GoogleId!
        _state.ConnectedRiders[googleId] = new RiderSession
        {
            ConnectionId = Context.ConnectionId,
            UserName = userName,
            GoogleId = googleId,
            GroupName = groupName,
            Role = "Rider"
        };

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
        // FIX: Look up the rider by their current ConnectionId
        var rider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
        {
            rider.ConnectionId = string.Empty; // Mark Offline in RAM (Do NOT remove them from dictionary)
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
        // FIX: Look up rider, then remove by GoogleId
        var rider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
        {
            _state.ConnectedRiders.TryRemove(rider.GoogleId, out _);
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
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
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
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
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
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
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
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
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

    // --- UPGRADED: TELEMETRY ENGINE WITH SMART COOLDOWNS ---
    public async Task UpdateMyLocation(string groupName, string userName, double lat, double lng, double heading)
    {
        await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveRiderLocation", userName, lat, lng, heading);

        var currentRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (session != null && currentRider != null)
        {
            currentRider.LastLat = lat;
            currentRider.LastLng = lng;

            string leadGoogleId = session.Settings.LeadRiderGoogleId;
            if (string.IsNullOrEmpty(leadGoogleId)) leadGoogleId = session.AdminGoogleId;

            if (currentRider.GoogleId != leadGoogleId)
            {
                var leadRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == leadGoogleId);
                if (leadRider != null && leadRider.LastLat != 0)
                {
                    double distance = CalculateDistanceMeters(lat, lng, leadRider.LastLat, leadRider.LastLng);
                    string lagKey = $"lag_{groupName}_{currentRider.GoogleId}";

                    if (distance > session.Settings.MaxLagDistanceMeters)
                    {
                        // COOLDOWN: Only alert if it's their first time lagging, or if 3 minutes have passed since the last alert
                        if (!_alertCooldowns.TryGetValue(lagKey, out var lastAlert) || (DateTime.UtcNow - lastAlert).TotalMinutes >= 3)
                        {
                            _alertCooldowns[lagKey] = DateTime.UtcNow; // Record the time we triggered this alert

                            var adminConn = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == session.AdminGoogleId)?.ConnectionId;
                            if (!string.IsNullOrEmpty(adminConn))
                            {
                                await Clients.Client(adminConn).SendAsync("ReceiveAlert", "Lagging", $"{userName} is {Math.Round(distance)} meters behind.");
                            }
                        }
                    }
                    else
                    {
                        // SMART RESET: They caught back up to the group! Clear the cooldown.
                        // This ensures if they fall behind again 10 seconds later, we instantly alert the admin again.
                        _alertCooldowns.TryRemove(lagKey, out _);
                    }
                }
            }
            else
            {
                double maxDist = 0;
                foreach (var r in _state.ConnectedRiders.Values.Where(x => x.GroupName == groupName && x.GoogleId != leadGoogleId))
                {
                    if (r.LastLat != 0)
                    {
                        double d = CalculateDistanceMeters(lat, lng, r.LastLat, r.LastLng);
                        if (d > maxDist) maxDist = d;
                    }
                }

                string splinterKey = $"splinter_{groupName}";

                if (maxDist > session.Settings.SplinterWarningDistanceMeters)
                {
                    // COOLDOWN: Only complain about the convoy being splintered once every 5 minutes
                    if (!_alertCooldowns.TryGetValue(splinterKey, out var lastAlert) || (DateTime.UtcNow - lastAlert).TotalMinutes >= 5)
                    {
                        _alertCooldowns[splinterKey] = DateTime.UtcNow;
                        await Clients.Client(currentRider.ConnectionId).SendAsync("ReceiveAlert", "Splinter", "Convoy splintered! A rider has fallen too far behind.");
                    }
                }
                else
                {
                    // SMART RESET: Convoy has safely regrouped.
                    _alertCooldowns.TryRemove(splinterKey, out _);
                }
            }
        }
    }

    // --- NEW: PUSH TO TALK (PTT) LOGIC ---
    public async Task RequestPtt(string groupName, string userName)
    {
        if (!_state.ActiveSpeakers.ContainsKey(groupName))
        {
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

        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        // FIX: Link the new connection back to the persistent GoogleId entry
        if (_state.ConnectedRiders.TryGetValue(googleId, out var existingRider))
        {
            existingRider.ConnectionId = newConnectionId;
        }
        else
        {
            _state.ConnectedRiders[googleId] = new RiderSession { ConnectionId = newConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName, Role = "Rider" };
        }

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

    public async Task UpdateGroupSettings(string groupName, int maxLag, int splinterDist, int maxSize)
    {
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            // NEW: Prevent lowering the max size below the current active member count
            int currentMembers = _state.ConnectedRiders.Values.Count(r => r.GroupName == groupName);
            if (maxSize < currentMembers)
            {
                throw new HubException($"Cannot reduce the maximum group size below the current active member count ({currentMembers}).");
            }

            var update = Builders<GroupSession>.Update
                .Set(g => g.Settings.MaxLagDistanceMeters, maxLag)
                .Set(g => g.Settings.SplinterWarningDistanceMeters, splinterDist)
                .Set(g => g.Settings.MaxGroupSize, maxSize);

            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.All.SendAsync("GroupsUpdated");
        }
    }

    public async Task AssignRole(string groupName, string targetGoogleId, string newRole)
    {
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            // Update role directly using GoogleId
            if (_state.ConnectedRiders.TryGetValue(targetGoogleId, out var targetSession) && targetSession.GroupName == groupName)
            {
                targetSession.Role = newRole;

                if (newRole == "Lead")
                {
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.Settings.LeadRiderGoogleId, targetGoogleId));
                }
                else if (newRole == "Tail")
                {
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.Settings.SweepRiderGoogleId, targetGoogleId));
                }

                await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveLobby();
        // FIX: Removed the line that deletes the rider from the ConcurrentDictionary!
        // The user is merely offline now. RestoreConnectionState will pick them right back up!
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