using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Backend.Hubs;

// NEW: Local class to track speed and odometers in RAM
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
    // NEW: Tracks real-time speeds and distances
    private static readonly ConcurrentDictionary<string, RiderTelemetry> _telemetryStats = new();

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
                    Role = rider.Role
                });
            }
        }
        return roster;
    }

    // --- AUTHENTICATION & USER REGISTRY ---
    public async Task<UserProfileDto?> AuthenticateUser(string googleId)
    {
        var account = await _state.UserAccounts.Find(u => u.GoogleId == googleId).FirstOrDefaultAsync();
        if (account == null) return null;

        // DECRYPT before sending back to the owning user
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

        // ENCRYPT the PII fields before they touch the database
        var update = Builders<UserAccount>.Update
            .Set(u => u.Username, profile.Username.Trim())
            .Set(u => u.EmergencyContact, EncryptionHelper.Encrypt(profile.EmergencyContact?.Trim() ?? ""))
            .Set(u => u.VehicleNumber, EncryptionHelper.Encrypt(profile.VehicleNumber?.Trim() ?? ""))
            .Set(u => u.BloodGroup, EncryptionHelper.Encrypt(profile.BloodGroup ?? ""))
            .Set(u => u.HasConsented, profile.HasConsented);

        await _state.UserAccounts.UpdateOneAsync(
            u => u.GoogleId == googleId,
            update,
            new UpdateOptions { IsUpsert = true }
        );

        return true;
    }

    // NEW: Secure Admin Endpoint for Emergency Access
    public async Task<UserProfileDto?> GetRiderEmergencyInfo(string targetGoogleId)
    {
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (caller == null) return null;

        // 1. Verify the target is actually in the caller's group
        var targetSession = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == targetGoogleId);
        if (targetSession == null || targetSession.GroupName != caller.GroupName)
            throw new HubException("Target rider is not in your group.");

        // 2. Verify the caller is the strict Admin of that group
        var groupSession = await _state.ActiveGroups.Find(g => g.GroupName == caller.GroupName).FirstOrDefaultAsync();
        if (groupSession == null || groupSession.AdminGoogleId != caller.GoogleId)
            throw new HubException("Access Denied: Only the Group Admin can view emergency information.");

        // 3. Fetch and decrypt the profile
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
        var owner = await _state.UserAccounts.Find(u => u.Username.ToLower() == desiredUsername.ToLower()).FirstOrDefaultAsync();
        if (owner != null && (string.IsNullOrEmpty(currentGoogleId) || owner.GoogleId != currentGoogleId))
            throw new HubException($"The username '{desiredUsername}' is already taken.");

        string googleIdToUse = string.IsNullOrEmpty(currentGoogleId) ? Guid.NewGuid().ToString() : currentGoogleId;
        var newAccount = new UserAccount { GoogleId = googleIdToUse, Username = desiredUsername };

        await _state.UserAccounts.ReplaceOneAsync(u => u.GoogleId == googleIdToUse, newAccount, new ReplaceOptions { IsUpsert = true });
        return googleIdToUse;
    }

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
                LeadRiderGoogleId = googleId,
                PitstopDistanceMeters = 100000, // Default 100km
            }
        };

        await _state.ActiveGroups.InsertOneAsync(session);

        _state.ConnectedRiders[googleId] = new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName, Role = "Lead" };

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

        _state.ConnectedRiders[googleId] = new RiderSession { ConnectionId = Context.ConnectionId, UserName = userName, GoogleId = googleId, GroupName = groupName, Role = "Rider" };

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("UserJoinedAlert", userName);
        await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));

        if (session.IsNavigating)
        {
            // IDEA 1: Roster Voice Announcement
            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"{userName} has joined the convoy.");
            await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName);
        }
        else if (!string.IsNullOrEmpty(session.DestName))
            await Clients.Caller.SendAsync("DestinationSet", session.DestLat, session.DestLng, session.DestName);
    }

    public async Task LeaveLobby()
    {
        var rider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
        {
            rider.ConnectionId = string.Empty;
            await Clients.Group(rider.GroupName).SendAsync("UserOfflineAlert", rider.UserName);
            await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", await GetGroupRoster(rider.GroupName));

            // IDEA 1: Dead-zone Voice Announcement
            await Clients.Group(rider.GroupName).SendAsync("ReceiveAlert", "VoicePrompt", $"Warning. {rider.UserName} has lost connection.");

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

    // UPDATE: Now requires googleId
    public async Task LeaveGroup(string googleId)
    {
        // FIX: Look up rider directly by GoogleId
        if (_state.ConnectedRiders.TryGetValue(googleId, out var rider))
        {
            _state.ConnectedRiders.TryRemove(googleId, out _);
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

    // UPDATE: Now requires googleId
    public async Task DeleteGroup(string groupName, string googleId)
    {
        // FIX: Instant O(1) lookup using GoogleId
        if (!_state.ConnectedRiders.TryGetValue(googleId, out var caller))
            throw new HubException("Unauthenticated request.");

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

    public async Task SetDestination(string groupName, double destLat, double destLng, string destName)
    {
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            var update = Builders<GroupSession>.Update.Set(g => g.DestLat, destLat).Set(g => g.DestLng, destLng).Set(g => g.DestName, destName);
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.Group(groupName).SendAsync("DestinationSet", destLat, destLng, destName);

            // IDEA 2: Route Syncing Voice
            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"The Lead rider has set a new destination: {destName}.");
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

    // --- UPGRADED: TELEMETRY ENGINE WITH FULL 4-PILLAR VOICE INTEGRATION ---
    public async Task UpdateMyLocation(string groupName, string userName, double lat, double lng, double heading)
    {
        await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveRiderLocation", userName, lat, lng, heading);

        var currentRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (session != null && currentRider != null)
        {
            // 1. Calculate Real-Time Speed & Distance (IDEA 4: Telemetry Math)
            var telemetry = _telemetryStats.GetOrAdd(currentRider.GoogleId, new RiderTelemetry());

            if (currentRider.LastLat != 0)
            {
                double distanceMoved = CalculateDistanceMeters(currentRider.LastLat, currentRider.LastLng, lat, lng);
                var timeDelta = (DateTime.UtcNow - telemetry.LastUpdate).TotalSeconds;

                if (timeDelta > 0 && distanceMoved < 1000) // Sanity check for GPS jumps
                {
                    telemetry.CurrentSpeedKmh = (distanceMoved / timeDelta) * 3.6;
                }

                // IDEA 4: Pitstop Tracker (Fires every 100km crossed)
                double oldDistance = telemetry.TotalDistanceMeters;
                telemetry.TotalDistanceMeters += distanceMoved;
                int pitstopIntervalMeters = session.Settings.PitstopDistanceMeters;
                // Only alert if the admin didn't set the slider to 0 (Off)
                if (pitstopIntervalMeters > 0 && (int)(oldDistance / pitstopIntervalMeters) < (int)(telemetry.TotalDistanceMeters / pitstopIntervalMeters))
                {
                    await Clients.Client(Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"You have traveled {(int)(telemetry.TotalDistanceMeters / 1000)} kilometers. Consider pulling over for a rest stop.");
                }

                // IDEA 4: Speed Delta Warning (Only fire if going 30kmh over group average)
                var activeRiders = _state.ConnectedRiders.Values.Where(r => r.GroupName == groupName).Select(r => r.GoogleId).ToList();
                var groupSpeeds = _telemetryStats.Where(k => activeRiders.Contains(k.Key) && k.Value.CurrentSpeedKmh > 10).Select(k => k.Value.CurrentSpeedKmh).ToList();

                if (groupSpeeds.Count > 1)
                {
                    double avgSpeed = groupSpeeds.Average();
                    if (telemetry.CurrentSpeedKmh > avgSpeed + 30)
                    {
                        string speedKey = $"speed_{currentRider.GoogleId}";
                        if (!_alertCooldowns.TryGetValue(speedKey, out var lastSpd) || (DateTime.UtcNow - lastSpd).TotalMinutes >= 10)
                        {
                            _alertCooldowns[speedKey] = DateTime.UtcNow;
                            await Clients.Client(Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", "Warning: You are riding significantly faster than the group average. Please slow down.");
                        }
                    }
                }
            }

            telemetry.LastUpdate = DateTime.UtcNow;
            currentRider.LastLat = lat;
            currentRider.LastLng = lng;

            // 2. Proximity & Splinter Math
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
                        if (!_alertCooldowns.TryGetValue(lagKey, out var lastAlert) || (DateTime.UtcNow - lastAlert).TotalMinutes >= 3)
                        {
                            _alertCooldowns[lagKey] = DateTime.UtcNow;
                            var adminConn = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == session.AdminGoogleId)?.ConnectionId;
                            if (!string.IsNullOrEmpty(adminConn))
                            {
                                // Changed to VoicePrompt so it reads seamlessly
                                await Clients.Client(adminConn).SendAsync("ReceiveAlert", "VoicePrompt", $"{userName} is {Math.Round(distance)} meters behind.");
                            }
                        }
                    }
                    else { _alertCooldowns.TryRemove(lagKey, out _); }
                }
            }
            else
            {
                // This is the Lead Rider
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
                    if (!_alertCooldowns.TryGetValue(splinterKey, out var lastAlert) || (DateTime.UtcNow - lastAlert).TotalMinutes >= 5)
                    {
                        _alertCooldowns[splinterKey] = DateTime.UtcNow;
                        await Clients.Client(currentRider.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", "Convoy splintered! A rider has fallen too far behind.");
                    }
                }
                else { _alertCooldowns.TryRemove(splinterKey, out _); }

                // IDEA 2: Arrival Detection
                if (session.IsNavigating)
                {
                    double distToDest = CalculateDistanceMeters(lat, lng, session.DestLat, session.DestLng);
                    if (distToDest < session.Settings.ArrivalGeofenceMeters)
                    {
                        string arrivalKey = $"arrival_{groupName}";
                        if (!_alertCooldowns.ContainsKey(arrivalKey))
                        {
                            _alertCooldowns[arrivalKey] = DateTime.UtcNow;
                            await Clients.Group(groupName).SendAsync("ReceiveAlert", "VoicePrompt", $"The Lead rider is arriving at {session.DestName}.");
                        }
                    }
                }
            }
        }
    }
    // 2. NEW: Method to retrieve the current settings from the DB
    public async Task<GroupSettingsDto> GetGroupSettings(string groupName)
    {
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session != null)
        {
            return new GroupSettingsDto
            {
                MaxLagDistanceMeters = session.Settings.MaxLagDistanceMeters,
                SplinterWarningDistanceMeters = session.Settings.SplinterWarningDistanceMeters,
                MaxGroupSize = session.Settings.MaxGroupSize,
                PitstopDistanceMeters = session.Settings.PitstopDistanceMeters
            };
        }
        return null;
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

    public async Task RestoreConnectionState(string googleId, string userName, string groupName)
    {
        var newConnectionId = Context.ConnectionId;
        if (string.IsNullOrEmpty(groupName)) return;

        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (_state.ConnectedRiders.TryGetValue(googleId, out var existingRider))
        {
            existingRider.ConnectionId = newConnectionId;
            // IDEA 1: Reconnection Voice
            await Clients.GroupExcept(groupName, newConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"{userName} has reconnected.");
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

    public async Task UpdateGroupSettings(string groupName, int maxLag, int splinterDist, int maxSize, int pitstopDist)
    {
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            int currentMembers = _state.ConnectedRiders.Values.Count(r => r.GroupName == groupName);
            if (maxSize < currentMembers)
                throw new HubException($"Cannot reduce the maximum group size below the current active member count ({currentMembers}).");

            var update = Builders<GroupSession>.Update
                .Set(g => g.Settings.MaxLagDistanceMeters, maxLag)
                .Set(g => g.Settings.SplinterWarningDistanceMeters, splinterDist)
                .Set(g => g.Settings.MaxGroupSize, maxSize)
                .Set(g => g.Settings.PitstopDistanceMeters, pitstopDist); // Sync to Database

            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.All.SendAsync("GroupsUpdated");
        }
    }

    public async Task AssignRole(string groupName, string targetGoogleId, string newRole)
    {
        if (string.IsNullOrEmpty(targetGoogleId)) return;

        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            if (_state.ConnectedRiders.TryGetValue(targetGoogleId, out var targetSession) && targetSession.GroupName == groupName)
            {
                targetSession.Role = newRole;

                if (newRole == "Lead")
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.Settings.LeadRiderGoogleId, targetGoogleId));
                else if (newRole == "Tail")
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.Settings.SweepRiderGoogleId, targetGoogleId));

                await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));

                // IDEA 3: Role Assignment Voice Announcement
                await Clients.Client(targetSession.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"You have been designated as the {newRole} rider.");
            }
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