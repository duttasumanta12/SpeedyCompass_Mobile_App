using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using SpeedyCompass.Backend.Models;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpeedyCompass.Backend.Hubs;

public class RiderTelemetry
{
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
    public double CurrentSpeedKmh { get; set; }
    public double TotalDistanceMeters { get; set; }
}

public class CompassHub : Hub
{
    private readonly CompassStateManager _state;
    private static readonly ConcurrentDictionary<string, DateTime> _alertCooldowns = new();
    private static readonly ConcurrentDictionary<string, RiderTelemetry> _telemetryStats = new();

    public CompassHub(CompassStateManager state)
    {
        _state = state;
    }

    public async Task<List<GroupMember>> GetGroupRoster(string groupName)
    {
        // 100% Database Driven - No more ConnectedRiders RAM dictionary!
        return await _state.GroupMembers.Find(m => m.GroupName == groupName).ToListAsync();
    }

    public async Task<UserProfileDto?> AuthenticateUser(string googleId)
    {
        var account = await _state.UserAccounts.Find(u => u.GoogleId == googleId).FirstOrDefaultAsync();
        if (account == null) return null;

        return new UserProfileDto
        {
            Username = account.Username,
            EmergencyContact = EncryptionHelper.Decrypt(account.EmergencyContact),
            VehicleNumber = EncryptionHelper.Decrypt(account.VehicleNumber),
            BloodGroup = EncryptionHelper.Decrypt(account.BloodGroup),
            HasConsented = account.HasConsented
        };
    }

    public async Task<bool> SaveUserProfile(string googleId, UserProfileDto profile)
    {
        if (string.IsNullOrEmpty(googleId) || string.IsNullOrWhiteSpace(profile.Username))
            throw new HubException("Invalid profile data.");

        var owner = await _state.UserAccounts.Find(u => u.Username.ToLower() == profile.Username.ToLower()).FirstOrDefaultAsync();
        if (owner != null && owner.GoogleId != googleId)
        {
            throw new HubException($"The username '{profile.Username}' is already taken.");
        }

        var update = Builders<UserAccount>.Update
            .Set(u => u.Username, profile.Username.Trim())
            .Set(u => u.EmergencyContact, EncryptionHelper.Encrypt(profile.EmergencyContact?.Trim() ?? ""))
            .Set(u => u.VehicleNumber, EncryptionHelper.Encrypt(profile.VehicleNumber?.Trim() ?? ""))
            .Set(u => u.BloodGroup, EncryptionHelper.Encrypt(profile.BloodGroup ?? ""))
            .Set(u => u.HasConsented, profile.HasConsented);

        await _state.UserAccounts.UpdateOneAsync(u => u.GoogleId == googleId, update, new UpdateOptions { IsUpsert = true });
        return true;
    }

    public async Task<UserProfileDto?> GetRiderEmergencyInfo(string targetGoogleId)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        if (caller == null) return null;

        var targetSession = await _state.GroupMembers.Find(m => m.GoogleId == targetGoogleId).FirstOrDefaultAsync();
        if (targetSession == null || targetSession.GroupName != caller.GroupName)
            throw new HubException("Target rider is not in your group.");

        var groupSession = await _state.GetGroupCachedAsync(caller.GroupName);
        if (groupSession == null || groupSession.AdminGoogleId != caller.GoogleId)
            throw new HubException("Access Denied: Only the Group Admin can view emergency information.");

        var account = await _state.UserAccounts.Find(u => u.GoogleId == targetGoogleId).FirstOrDefaultAsync();
        if (account == null) return null;

        return new UserProfileDto
        {
            Username = account.Username,
            EmergencyContact = EncryptionHelper.Decrypt(account.EmergencyContact),
            VehicleNumber = EncryptionHelper.Decrypt(account.VehicleNumber),
            BloodGroup = EncryptionHelper.Decrypt(account.BloodGroup)
        };
    }

    public async Task<string> RegisterOrUpdateUser(string currentGoogleId, string desiredUsername)
    {
        string googleIdToUse = string.IsNullOrEmpty(currentGoogleId) ? Guid.NewGuid().ToString() : currentGoogleId;
        var existingUser = await _state.UserAccounts.Find(u => u.GoogleId == googleIdToUse).FirstOrDefaultAsync();

        if (existingUser != null)
        {
            if (existingUser.Username.Equals(desiredUsername, StringComparison.OrdinalIgnoreCase))
                return googleIdToUse;

            bool nameExists = await _state.UserAccounts.Find(u => u.Username.ToLower() == desiredUsername.ToLower() && u.GoogleId != googleIdToUse).AnyAsync();
            if (!nameExists)
            {
                var update = Builders<UserAccount>.Update.Set(u => u.Username, desiredUsername);
                await _state.UserAccounts.UpdateOneAsync(u => u.GoogleId == googleIdToUse, update);
            }
            else
            {
                throw new HubException($"The username '{desiredUsername}' is already taken by another rider.");
            }
        }
        else
        {
            string finalUsername = desiredUsername;
            bool nameExists = await _state.UserAccounts.Find(u => u.Username.ToLower() == finalUsername.ToLower()).AnyAsync();

            if (nameExists)
            {
                var random = new Random();
                while (true)
                {
                    finalUsername = $"{desiredUsername}{random.Next(1000, 10000)}";
                    bool stillTaken = await _state.UserAccounts.Find(u => u.Username.ToLower() == finalUsername.ToLower()).AnyAsync();
                    if (!stillTaken) break;
                }
            }

            var newAccount = new UserAccount { GoogleId = googleIdToUse, Username = finalUsername };
            await _state.UserAccounts.InsertOneAsync(newAccount);
        }

        return googleIdToUse;
    }

    public async Task<GroupDetailsDto> GetGroupDetails(string groupName)
    {
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session != null)
        {
            return new GroupDetailsDto
            {
                CurrentState = session.CurrentState,
                DestName = session.DestName ?? string.Empty,
                DestLat = session.DestLat,
                DestLng = session.DestLng,
                AdminGoogleId = session.AdminGoogleId,
                GroupName = groupName,
                JoinCode = session.JoinCode,
                Settings = await GetGroupSettings(groupName)
            };
        }
        return null;
    }

    public async Task CreateGroup(string groupName, string username, string googleId, string joinCode, GroupSettingsDto initialSettings)
    {
        var currentlyInGroup = await _state.GroupMembers.Find(m => m.GoogleId == googleId).AnyAsync();
        if (currentlyInGroup) throw new HubException("You are already in a group. Please leave it first.");

        var existing = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (existing != null) throw new HubException("Group name is already taken.");

        var newGroup = new GroupSession
        {
            GroupName = groupName,
            AdminGoogleId = googleId,
            JoinCode = joinCode,
            Settings = new GroupSettings
            {
                MaxGroupSize = initialSettings.MaxGroupSize,
                MaxLagDistanceMeters = initialSettings.MaxLagDistanceMeters,
                SplinterWarningDistanceMeters = initialSettings.SplinterWarningDistanceMeters,
                PitstopDistanceMeters = initialSettings.PitstopDistanceMeters
            },
            CurrentState = GroupState.NotNavigating
        };
        await _state.ActiveGroups.InsertOneAsync(newGroup);

        var member = new GroupMember
        {
            GroupName = groupName,
            GoogleId = googleId,
            Name = username,
            Role = "Admin",
            IsAdmin = true,
            IsOnline = true,
            ConnectionId = Context.ConnectionId,
            JoinedAt = DateTime.UtcNow
        };
        await _state.GroupMembers.InsertOneAsync(member);

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        await Clients.Caller.SendAsync("RosterUpdated", await GetGroupRoster(groupName));
    }

    public async Task JoinGroup(string groupName, string username, string googleId, string pinCode = null)
    {
        var group = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (group == null) throw new HubException("Group not found.");

        var existingMember = await _state.GroupMembers.Find(m => m.GroupName == groupName && m.GoogleId == googleId).FirstOrDefaultAsync();

        if (existingMember == null)
        {
            if (group.JoinCode != pinCode)
                throw new HubException("Incorrect Convoy PIN.");

            long currentCount = await _state.GroupMembers.CountDocumentsAsync(m => m.GroupName == groupName);
            if (currentCount >= group.Settings.MaxGroupSize)
                throw new HubException("This convoy is currently full.");

            var newMember = new GroupMember
            {
                GroupName = groupName,
                GoogleId = googleId,
                Name = username,
                Role = "Rider",
                IsAdmin = false,
                IsOnline = true,
                ConnectionId = Context.ConnectionId,
                JoinedAt = DateTime.UtcNow
            };
            await _state.GroupMembers.InsertOneAsync(newMember);

            await Clients.OthersInGroup(groupName).SendAsync("UserJoinedAlert", username);
        }
        else
        {
            var update = Builders<GroupMember>.Update
                .Set(m => m.IsOnline, true)
                .Set(m => m.ConnectionId, Context.ConnectionId);

            await _state.GroupMembers.UpdateOneAsync(m => m.Id == existingMember.Id, update);

            await Clients.OthersInGroup(groupName).SendAsync("UserJoinedAlert", username);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        var roster = await GetGroupRoster(groupName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", roster);
    }

    public async Task LeaveLobby()
    {
        var rider = await _state.GroupMembers.Find(r => r.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        if (rider != null)
        {
            var update = Builders<GroupMember>.Update
                .Set(m => m.ConnectionId, string.Empty)
                .Set(m => m.IsOnline, false);
            await _state.GroupMembers.UpdateOneAsync(m => m.Id == rider.Id, update);

            await Clients.Group(rider.GroupName).SendAsync("UserOfflineAlert", rider.Name);
            await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", await GetGroupRoster(rider.GroupName));
            await Clients.Group(rider.GroupName).SendAsync("ReceiveAlert", "VoicePrompt", $"Warning. {rider.Name} has lost connection.");

            var session = await _state.GetGroupCachedAsync(rider.GroupName);
            if (session != null)
            {
                if (_state.ActiveSpeakers.TryGetValue(rider.GroupName, out var activeSpeaker) && activeSpeaker == rider.Name)
                {
                    _state.ActiveSpeakers.TryRemove(rider.GroupName, out _);
                    await Clients.Group(rider.GroupName).SendAsync("PttReleased");
                }

                bool isAnyoneOnline = await _state.GroupMembers.Find(m => m.GroupName == rider.GroupName && m.IsOnline).AnyAsync();
                if (!isAnyoneOnline)
                {
                    var groupUpdate = Builders<GroupSession>.Update
                        .Set(g => g.CurrentState, string.IsNullOrEmpty(session.DestName) ? GroupState.NotNavigating : GroupState.DestinationSet);

                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == rider.GroupName, groupUpdate);

                    var allRiders = await _state.GroupMembers.Find(m => m.GroupName == rider.GroupName).ToListAsync();
                    foreach (var member in allRiders)
                    {
                        _telemetryStats.TryRemove(member.GoogleId, out _);
                    }
                }
            }
        }
    }

    public async Task LeaveGroup(string googleId)
    {
        var rider = await _state.GroupMembers.Find(m => m.GoogleId == googleId).FirstOrDefaultAsync();
        if (rider != null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, rider.GroupName);
            await _state.GroupMembers.DeleteOneAsync(m => m.Id == rider.Id);

            var session = await _state.GetGroupCachedAsync(rider.GroupName);
            if (session != null && session.AdminGoogleId == rider.GoogleId)
            {
                await Clients.Group(rider.GroupName).SendAsync("GroupDeleted");
                await _state.ActiveGroups.DeleteOneAsync(g => g.GroupName == rider.GroupName);
                await _state.GroupMembers.DeleteManyAsync(m => m.GroupName == rider.GroupName);
            }
            else
            {
                await Clients.Group(rider.GroupName).SendAsync("UserLeftAlert", rider.Name);
                await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", await GetGroupRoster(rider.GroupName));
            }
        }
    }

    public async Task DeleteGroup(string groupName, string googleId)
    {
        var caller = await _state.GroupMembers.Find(m => m.GoogleId == googleId && m.GroupName == groupName).FirstOrDefaultAsync();
        if (caller == null) throw new HubException("Unauthenticated request.");

        var session = await _state.GetGroupCachedAsync(groupName);
        if (session != null && session.AdminGoogleId == caller.GoogleId)
        {
            await Clients.Group(groupName).SendAsync("GroupDeleted");
            await _state.ActiveGroups.DeleteOneAsync(g => g.GroupName == groupName);
            await _state.GroupMembers.DeleteManyAsync(m => m.GroupName == groupName);
        }
        else throw new HubException("Only the group admin can delete this group.");
    }

    public async Task SetDestination(string groupName, double destLat, double destLng, string destName)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            var update = Builders<GroupSession>.Update
                .Set(g => g.CurrentState, GroupState.DestinationSet)
                .Set(g => g.DestLat, destLat)
                .Set(g => g.DestLng, destLng)
                .Set(g => g.DestName, destName);
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);

            await Clients.Group(groupName).SendAsync("DestinationSet", destLat, destLng, destName);
            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"The Lead rider has set a new destination: {destName}.");
        }
    }

    public async Task StartNavigation(string groupName, double destLat, double destLng, string destName)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            var update = Builders<GroupSession>.Update
                .Set(g => g.CurrentState, GroupState.Navigating)
                .Set(g => g.DestLat, destLat)
                .Set(g => g.DestLng, destLng)
                .Set(g => g.DestName, destName);

            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.Group(groupName).SendAsync("NavigationStarted", destLat, destLng, destName, false);
        }
    }

    public async Task CancelNavigation(string groupName)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.CurrentState, GroupState.NotNavigating));

            var ridersInGroup = await _state.GroupMembers.Find(m => m.GroupName == groupName).ToListAsync();
            foreach (var r in ridersInGroup)
            {
                _telemetryStats.TryRemove(r.GoogleId, out _);
            }

            await Clients.Group(groupName).SendAsync("NavigationCancelled");
        }
    }

    public async Task SendGroupAlert(string groupName, string alertType, string senderName)
    {
        var session = await _state.GetGroupCachedAsync(groupName);
        if (session != null) await Clients.Group(groupName).SendAsync("ReceiveAlert", alertType, senderName);
    }

    // --- STRIPPED DOWN: LIGHTNING FAST LOCATION UPDATE ---
    public async Task UpdateMyLocation(string groupName, string userName, double lat, double lng, double heading)
    {
        // 1. Instantly broadcast to others (Zero math delay)
        await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveRiderLocation", userName, lat, lng, heading);

        // 2. Fire-and-forget DB Update
        var currentRider = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        if (currentRider != null)
        {
            var updateCoords = Builders<GroupMember>.Update
                .Set(m => m.LastLat, lat)
                .Set(m => m.LastLng, lng)
                .Set(m => m.Heading, heading)
                .Set(m => m.LastUpdate, DateTime.UtcNow);

            await _state.GroupMembers.UpdateOneAsync(m => m.Id == currentRider.Id, updateCoords);
        }
    }
    // =======================================================
    // --- NEW: EDGE COMPUTING RELAY ENDPOINTS ---
    // =======================================================

    public async Task RelayLagWarning(string groupName, string userName, double distanceMeters, bool isAhead)
    {
        var session = await _state.GetGroupCachedAsync(groupName);
        if (session == null) return;

        string distText = distanceMeters > 1000 ? $"{Math.Round(distanceMeters / 1000.0, 1)} kilometers" : $"{Math.Round(distanceMeters)} meters";
        string statusText = isAhead ? "ahead of" : "behind";
        string msg = $"{userName} is {distText} {statusText} the Lead.";

        // Send to Leadership ONLY
        var leadershipConns = await _state.GroupMembers.Find(r =>
            r.GroupName == groupName && r.IsOnline &&
            (r.GoogleId == session.AdminGoogleId || r.Role == "Lead" || r.Role == "Marshal" || r.Role == "Tail")
        ).ToListAsync();

        foreach (var leader in leadershipConns)
        {
            if (!string.IsNullOrEmpty(leader.ConnectionId))
                await Clients.Client(leader.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", msg);
        }
    }

    public async Task RelaySplinterWarning(string groupName)
    {
        await Clients.Group(groupName).SendAsync("ReceiveAlert", "VoicePrompt", "Convoy splintered! The group is stretched too far.");
    }

    public async Task RelayPitstopReminder(string groupName, double distanceKm)
    {
        await Clients.Group(groupName).SendAsync("ReceiveAlert", "VoicePrompt", $"The Lead has traveled {Math.Round(distanceKm)} kilometers. Consider a group rest stop.");
    }

    public async Task RelayArrivalAlert(string groupName)
    {
        await Clients.Group(groupName).SendAsync("ReceiveAlert", "VoicePrompt", "The Lead is arriving at the destination.");
    }

    public async Task NotifyRouteDeviation(string groupName, string userName)
    {
        var session = await _state.GetGroupCachedAsync(groupName);
        if (session != null && session.Settings.EnableDynamicRouting)
        {
            // Find the leadership team and warn them that a user took a wrong turn
            var leadershipConns = await _state.GroupMembers.Find(m =>
                m.GroupName == groupName &&
                m.IsOnline &&
                (m.Role == "Admin" || m.Role == "Lead" || m.Role == "Marshal" || m.Role == "Tail")
            ).ToListAsync();

            foreach (var leader in leadershipConns)
            {
                if (!string.IsNullOrEmpty(leader.ConnectionId))
                {
                    await Clients.Client(leader.ConnectionId).SendAsync("ReceiveRouteDeviation", userName);
                    await Clients.Client(leader.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"Warning. {userName} has deviated from the established route.");
                }
            }
        }
    }

    public async Task<List<TelemetryDto>> GetGroupTelemetry(string groupName)
    {
        var result = new List<TelemetryDto>();
        var session = await _state.GetGroupCachedAsync(groupName);
        if (session == null) return result;

        var allMembers = await _state.GroupMembers.Find(m => m.GroupName == groupName).ToListAsync();
        string leadGoogleId = string.IsNullOrEmpty(session.Settings.LeadRiderGoogleId) ? session.AdminGoogleId : session.Settings.LeadRiderGoogleId;
        var leadRider = allMembers.FirstOrDefault(r => r.GoogleId == leadGoogleId);

        foreach (var rider in allMembers)
        {
            double speed = _telemetryStats.TryGetValue(rider.GoogleId, out var stats) ? stats.CurrentSpeedKmh : 0;
            double lagDistance = 0;
            string status = "In Formation";

            if (leadRider != null && rider.GoogleId != leadGoogleId && rider.LastLat != 0 && leadRider.LastLat != 0)
            {
                if (session.CurrentState == GroupState.Navigating)
                {
                    double leadToDest = CalculateDistanceMeters(leadRider.LastLat, leadRider.LastLng, session.DestLat, session.DestLng);
                    double riderToDest = CalculateDistanceMeters(rider.LastLat, rider.LastLng, session.DestLat, session.DestLng);
                    double delta = riderToDest - leadToDest;

                    if (delta > 0)
                    {
                        lagDistance = delta;
                        status = "Behind";
                    }
                    else if (delta < 0)
                    {
                        lagDistance = Math.Abs(delta);
                        status = "Ahead";
                    }
                }
                else
                {
                    lagDistance = CalculateDistanceMeters(rider.LastLat, rider.LastLng, leadRider.LastLat, leadRider.LastLng);
                    status = lagDistance > 50 ? "Separated" : "Near Lead";
                }
            }
            else if (rider.GoogleId == leadGoogleId) { status = "Lead Rider"; }
            else if (rider.LastLat == 0) { status = "Awaiting GPS"; }

            result.Add(new TelemetryDto { Name = rider.Name, SpeedKmh = speed, DistanceMeters = lagDistance, Status = status });
        }

        return result.OrderBy(r => r.Status == "Behind").ThenByDescending(r => r.DistanceMeters).ToList();
    }

    public async Task<GroupSettingsDto> GetGroupSettings(string groupName)
    {
        var session = await _state.GetGroupCachedAsync(groupName);
        if (session != null)
        {
            return new GroupSettingsDto
            {
                MaxLagDistanceMeters = session.Settings.MaxLagDistanceMeters,
                SplinterWarningDistanceMeters = session.Settings.SplinterWarningDistanceMeters,
                MaxGroupSize = session.Settings.MaxGroupSize,
                PitstopDistanceMeters = session.Settings.PitstopDistanceMeters,
                EnableDynamicRouting = session.Settings.EnableDynamicRouting,
                ArrivalGeofenceMeters = session.Settings.ArrivalGeofenceMeters,
                LeadRiderGoogleId = session.Settings.LeadRiderGoogleId,
                // --- NEW ---
                MinUpdateDistanceMeters = session.Settings.MinUpdateDistanceMeters,
                MaxUpdateDistanceMeters = session.Settings.MaxUpdateDistanceMeters,
                GroupName = session.GroupName
            };
        }
        return null;
    }

    public async Task UpdateGroupSettings(string groupName, GroupSettingsDto newSettings)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            long currentMembers = await _state.GroupMembers.CountDocumentsAsync(m => m.GroupName == groupName);
            if (newSettings.MaxGroupSize < currentMembers)
                throw new HubException($"Cannot reduce the maximum group size below the current active member count ({currentMembers}).");

            var update = Builders<GroupSession>.Update
                .Set(g => g.Settings.MaxLagDistanceMeters, newSettings.MaxLagDistanceMeters)
                .Set(g => g.Settings.SplinterWarningDistanceMeters, newSettings.SplinterWarningDistanceMeters)
                .Set(g => g.Settings.MaxGroupSize, newSettings.MaxGroupSize)
                .Set(g => g.Settings.PitstopDistanceMeters, newSettings.PitstopDistanceMeters)
                .Set(g => g.Settings.EnableDynamicRouting, newSettings.EnableDynamicRouting)
                .Set(g => g.Settings.MinUpdateDistanceMeters, newSettings.MinUpdateDistanceMeters)
                .Set(g => g.Settings.MaxUpdateDistanceMeters, newSettings.MaxUpdateDistanceMeters);

            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);

            // --- NEW: Push the exact new settings object to everyone in the convoy! ---
            await Clients.Group(groupName).SendAsync("ReceiveGroupSettings", newSettings);
        }
    }

    public async Task AssignRole(string groupName, string targetGoogleId, string newRole)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            if (newRole == "Lead" || newRole == "Tail" || newRole == "Marshal")
            {
                var existingHolders = await _state.GroupMembers.Find(r => r.GroupName == groupName && r.Role == newRole && r.GoogleId != targetGoogleId).ToListAsync();

                foreach (var oldHolder in existingHolders)
                {
                    await _state.GroupMembers.UpdateOneAsync(m => m.Id == oldHolder.Id, Builders<GroupMember>.Update.Set(m => m.Role, "Rider"));
                    if (!string.IsNullOrEmpty(oldHolder.ConnectionId))
                    {
                        await Clients.Client(oldHolder.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"Your role has been reassigned. You are now a Standard Rider.");
                    }
                }
            }

            var targetRider = await _state.GroupMembers.Find(r => r.GroupName == groupName && r.GoogleId == targetGoogleId).FirstOrDefaultAsync();
            if (targetRider != null)
            {
                await _state.GroupMembers.UpdateOneAsync(m => m.Id == targetRider.Id, Builders<GroupMember>.Update.Set(m => m.Role, newRole));

                if (newRole == "Lead")
                {
                    var update = Builders<GroupSession>.Update.Set(g => g.Settings.LeadRiderGoogleId, targetGoogleId);
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
                }

                if (!string.IsNullOrEmpty(targetRider.ConnectionId) && targetRider.GoogleId != caller.GoogleId)
                {
                    await Clients.Client(targetRider.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"You have been designated as the {newRole}.");
                }

                await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));
            }
        }
    }

    public async Task RestoreConnectionState(string googleId, string userName, string groupName)
    {
        var newConnectionId = Context.ConnectionId;
        if (string.IsNullOrEmpty(groupName)) return;

        var session = await _state.GetGroupCachedAsync(groupName);
        var existingRider = await _state.GroupMembers.Find(m => m.GoogleId == googleId).FirstOrDefaultAsync();

        if (existingRider != null)
        {
            var update = Builders<GroupMember>.Update
                .Set(m => m.ConnectionId, newConnectionId)
                .Set(m => m.IsOnline, true)
                .Set(m => m.Name, userName);

            await _state.GroupMembers.UpdateOneAsync(m => m.Id == existingRider.Id, update);
            await Clients.GroupExcept(groupName, newConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"{userName} has reconnected.");
        }
        else
        {
            var newRider = new GroupMember { ConnectionId = newConnectionId, Name = userName, GoogleId = googleId, GroupName = groupName, Role = "Rider", IsOnline = true };
            await _state.GroupMembers.InsertOneAsync(newRider);
        }

        await Groups.AddToGroupAsync(newConnectionId, groupName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));

        if (session != null)
        {
            if (session.CurrentState == GroupState.Navigating)
                await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName, true);
            else if (!string.IsNullOrEmpty(session.DestName))
                await Clients.Caller.SendAsync("DestinationSet", session.DestLat, session.DestLng, session.DestName);
        }
    }

    public async Task PauseNavigation(string groupName, string reason, string adminName)
    {
        var pauseState = GroupStateHelper.GetBreakState(reason);
        var update = Builders<GroupSession>.Update.Set(g => g.CurrentState, pauseState);
        await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);

        await Clients.Group(groupName).SendAsync("ReceiveNavigationPaused", reason, adminName);
    }

    public async Task ResumeNavigation(string groupName, string adminName)
    {
        var update = Builders<GroupSession>.Update.Set(g => g.CurrentState, GroupState.Navigating);
        await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);

        await Clients.Group(groupName).SendAsync("ReceiveNavigationResumed", adminName);
    }

    public async Task CompleteNavigation(string groupName, string adminName)
    {
        var update = Builders<GroupSession>.Update
            .Set(g => g.CurrentState, GroupState.Completed)
            .Set(g => g.DestLat, 0)
            .Set(g => g.DestLng, 0)
            .Set(g => g.DestName, string.Empty);

        await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
        await Clients.Group(groupName).SendAsync("ReceiveNavigationCompleted", adminName);
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

    // --- WEBRTC MULTI-PEER MESH SIGNALING HUB ---

    public async Task SendOffer(string groupName, string targetGoogleId, string offerSdp)
    {
        var sender = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        if (sender != null)
        {
            await Clients.Group(groupName).SendAsync("ReceiveOffer", sender.GoogleId, offerSdp);
        }
    }

    public async Task SendAnswer(string groupName, string targetGoogleId, string answerSdp)
    {
        var sender = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        if (sender != null)
        {
            await Clients.Group(groupName).SendAsync("ReceiveAnswer", sender.GoogleId, answerSdp);
        }
    }

    public async Task SendIceCandidate(string groupName, string targetGoogleId, string candidateJson)
    {
        var sender = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        if (sender != null)
        {
            await Clients.Group(groupName).SendAsync("ReceiveIceCandidate", sender.GoogleId, candidateJson);
        }
    }
    // --- NEW: DYNAMIC ROUTING & MEETUP POINTS ---

    public async Task UpdateGroupRoute(string groupName, string encodedPolyline)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        // Only the Admin or designated Lead can force a route override for the whole group
        if (caller != null && session != null && session.Settings.EnableDynamicRouting)
        {
            if (caller.GoogleId == session.AdminGoogleId || caller.Role == "Lead")
            {
                // Send the new Polyline to everyone else so their maps instantly snap to the new route
                await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveNewLeadRoute", encodedPolyline);
                await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", "The Lead has recalculated the route. Updating your map.");
            }
        }
    }

    public async Task SetMeetupPoint(string groupName, double lat, double lng)
    {
        var caller = await _state.GroupMembers.Find(m => m.ConnectionId == Context.ConnectionId).FirstOrDefaultAsync();
        var session = await _state.GetGroupCachedAsync(groupName);

        if (caller != null && session != null && (caller.GoogleId == session.AdminGoogleId || caller.Role == "Lead"))
        {
            var update = Builders<GroupSession>.Update
                .Set(g => g.MeetupLat, lat)
                .Set(g => g.MeetupLng, lng)
                .Set(g => g.IsMeetupActive, true);

            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);

            await Clients.Group(groupName).SendAsync("ReceiveMeetupPoint", lat, lng);
            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", "A new meetup point has been established.");
        }
    }
}