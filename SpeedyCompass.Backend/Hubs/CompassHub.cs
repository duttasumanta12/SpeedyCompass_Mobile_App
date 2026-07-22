using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
        string googleIdToUse = string.IsNullOrEmpty(currentGoogleId) ? throw new HubException("Google Id cannot be null or empty string."): currentGoogleId;

        // 1. Check if user exists by Google ID
        var existingUser = await _state.UserAccounts.Find(u => u.GoogleId == googleIdToUse).FirstOrDefaultAsync();

        if (existingUser != null)
        {
            // --- IF YES: User exists in the Database ---

            // Check if the current username and the provided username are the same
            if (existingUser.Username.Equals(desiredUsername, StringComparison.OrdinalIgnoreCase))
            {
                return googleIdToUse; // Same name, no update needed!
            }

            // If they are different, check if the NEW username already exists for someone else
            bool nameExists = await _state.UserAccounts.Find(u => u.Username.ToLower() == desiredUsername.ToLower() && u.GoogleId != googleIdToUse).AnyAsync();

            if (!nameExists)
            {
                // If the name is free, update it!
                var update = Builders<UserAccount>.Update.Set(u => u.Username, desiredUsername);
                await _state.UserAccounts.UpdateOneAsync(u => u.GoogleId == googleIdToUse, update);
            }
            else
            {
                // We reject updates if a veteran user tries to steal another existing user's exact name
                throw new HubException($"The username '{desiredUsername}' is already taken by another rider.");
            }
        }
        else
        {
            // --- IF NO: Brand new user registration ---

            string finalUsername = desiredUsername;

            // Check if the desired username is already taken by someone else
            bool nameExists = await _state.UserAccounts.Find(u => u.Username.ToLower() == finalUsername.ToLower()).AnyAsync();

            if (nameExists)
            {
                // Generate a new username by adding numbers at the end (e.g. "Rider" -> "Rider4921")
                var random = new Random();
                while (true)
                {
                    finalUsername = $"{desiredUsername}{random.Next(1000, 10000)}";

                    // Double check the newly generated name isn't somehow taken too
                    bool stillTaken = await _state.UserAccounts.Find(u => u.Username.ToLower() == finalUsername.ToLower()).AnyAsync();
                    if (!stillTaken) break;
                }
            }

            // Save the brand new account to MongoDB
            var newAccount = new UserAccount { GoogleId = googleIdToUse, Username = finalUsername };
            await _state.UserAccounts.InsertOneAsync(newAccount);
        }

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
                IsNavigating = session.CurrentState == Shared.Constants.GroupState.Navigating,
                MaxGroupSize = session.Settings.MaxGroupSize
            });
        }
        return list;
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
                GroupName = groupName
            };
        }
        return null;
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
            CurrentState = GroupState.NotNavigating,
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

        if (session.CurrentState == Shared.Constants.GroupState.Navigating)
        {
            // IDEA 1: Roster Voice Announcement
            await Clients.GroupExcept(groupName, Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"{userName} has joined the convoy.");
            await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName);
        }
        else if (!string.IsNullOrEmpty(session.DestName))
            await Clients.Caller.SendAsync("DestinationSet", session.DestLat, session.DestLng, session.DestName);
    }

    // --- LOBBY VS GROUP LIFECYCLE ---
    public async Task LeaveLobby()
    {
        var rider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        if (rider != null)
        {
            rider.ConnectionId = string.Empty;
            await Clients.Group(rider.GroupName).SendAsync("UserOfflineAlert", rider.UserName);
            await Clients.Group(rider.GroupName).SendAsync("RosterUpdated", await GetGroupRoster(rider.GroupName));

            // Dead-zone Voice Announcement
            await Clients.Group(rider.GroupName).SendAsync("ReceiveAlert", "VoicePrompt", $"Warning. {rider.UserName} has lost connection.");

            var session = await _state.ActiveGroups.Find(g => g.GroupName == rider.GroupName).FirstOrDefaultAsync();
            if (session != null)
            {
                if (_state.ActiveSpeakers.TryGetValue(rider.GroupName, out var activeSpeaker) && activeSpeaker == rider.UserName)
                {
                    _state.ActiveSpeakers.TryRemove(rider.GroupName, out _);
                    await Clients.Group(rider.GroupName).SendAsync("PttReleased");
                }

                // --- NEW: AUTO-CLEANUP ROUTE IF GROUP IS EMPTY ---
                // Check if there is ANYONE left in the group who still has an active connection
                bool isAnyoneOnline = _state.ConnectedRiders.Values.Any(r => r.GroupName == rider.GroupName && !string.IsNullOrEmpty(r.ConnectionId));

                if (!isAnyoneOnline)
                {
                    // Everyone is offline! Wipe the active navigation state so the next ride starts fresh.
                    var update = Builders<GroupSession>.Update
                        .Set(g => g.CurrentState, string.IsNullOrEmpty(session.DestName) ? GroupState.NotNavigating : GroupState.DestinationSet);

                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == rider.GroupName, update);

                    // Optional: Scrub stale telemetry data for this group's riders from memory
                    var groupRiderIds = _state.ConnectedRiders.Values.Where(r => r.GroupName == rider.GroupName).Select(r => r.GoogleId).ToList();
                    foreach (var id in groupRiderIds)
                    {
                        _telemetryStats.TryRemove(id, out _);
                    }
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
            var update = Builders<GroupSession>.Update
                .Set(g => g.CurrentState, GroupState.DestinationSet)
                .Set(g => g.DestLat, destLat)
                .Set(g => g.DestLng, destLng)
                .Set(g => g.DestName, destName);
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
            var update = Builders<GroupSession>.Update.Set(g => g.CurrentState, Shared.Constants.GroupState.Navigating).Set(g => g.DestLat, destLat).Set(g => g.DestLng, destLng).Set(g => g.DestName, destName);
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
            await Clients.Group(groupName).SendAsync("NavigationStarted", destLat, destLng, destName, false);
        }
    }

    public async Task CancelNavigation(string groupName)
    {
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, Builders<GroupSession>.Update.Set(g => g.CurrentState, Shared.Constants.GroupState.NotNavigating));

            // --- NEW: Reset telemetry stats (speed, distance) for all riders in the group ---
            var ridersInGroup = _state.ConnectedRiders.Values
                .Where(r => r.GroupName == groupName)
                .Select(r => r.GoogleId)
                .ToList();

            foreach (var googleId in ridersInGroup)
            {
                _telemetryStats.TryRemove(googleId, out _);
            }

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
            // 1. Calculate Real-Time Speed & Distance
            var telemetry = _telemetryStats.GetOrAdd(currentRider.GoogleId, new RiderTelemetry());

            if (currentRider.LastLat != 0)
            {
                double distanceMoved = CalculateDistanceMeters(currentRider.LastLat, currentRider.LastLng, lat, lng);
                var timeDelta = (DateTime.UtcNow - telemetry.LastUpdate).TotalSeconds;

                if (timeDelta > 0 && distanceMoved < 1000)
                    telemetry.CurrentSpeedKmh = (distanceMoved / timeDelta) * 3.6;

                double oldDistance = telemetry.TotalDistanceMeters;
                telemetry.TotalDistanceMeters += distanceMoved;

                int pitstopIntervalMeters = (session.Settings.PitstopDistanceMeters > 0 ? session.Settings.PitstopDistanceMeters : 100000);

                // Pitstop Warning (Personal)
                if (pitstopIntervalMeters > 0 && (int)(oldDistance / pitstopIntervalMeters) < (int)(telemetry.TotalDistanceMeters / pitstopIntervalMeters))
                {
                    await Clients.Client(Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"You have traveled {(int)(telemetry.TotalDistanceMeters / 1000)} kilometers. Consider a rest stop.");
                }

                // Speed Delta Warning (Personal)
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
                            await Clients.Client(Context.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", "Warning: You are riding significantly faster than the group average.");
                        }
                    }
                }
            }

            telemetry.LastUpdate = DateTime.UtcNow;
            currentRider.LastLat = lat;
            currentRider.LastLng = lng;

            // 2. PROXIMITY MATH (Ahead vs Behind)
            string leadGoogleId = session.Settings.LeadRiderGoogleId;
            if (string.IsNullOrEmpty(leadGoogleId)) leadGoogleId = session.AdminGoogleId;

            if (currentRider.GoogleId != leadGoogleId)
            {
                var leadRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == leadGoogleId);
                if (leadRider != null && leadRider.LastLat != 0)
                {
                    double lagDistance = 0;
                    bool isAhead = false;
                    bool isBehind = false;

                    // Only calculate Lag if it is turned ON (> 0)
                    if (session.Settings.MaxLagDistanceMeters > 0)
                    {
                        if (session.CurrentState == Shared.Constants.GroupState.Navigating)
                        {
                            double leadToDest = CalculateDistanceMeters(leadRider.LastLat, leadRider.LastLng, session.DestLat, session.DestLng);
                            double riderToDest = CalculateDistanceMeters(lat, lng, session.DestLat, session.DestLng);
                            double delta = riderToDest - leadToDest;

                            if (delta > session.Settings.MaxLagDistanceMeters)
                            {
                                isBehind = true;
                                lagDistance = delta;
                            }
                            else if (delta < -session.Settings.MaxLagDistanceMeters)
                            {
                                isAhead = true;
                                lagDistance = Math.Abs(delta);
                            }
                        }
                        else
                        {
                            double pureRadiusDist = CalculateDistanceMeters(lat, lng, leadRider.LastLat, leadRider.LastLng);
                            if (pureRadiusDist > session.Settings.MaxLagDistanceMeters)
                            {
                                isBehind = true;
                                lagDistance = pureRadiusDist;
                            }
                        }
                    }

                    string lagKey = $"lag_{groupName}_{currentRider.GoogleId}";

                    if (isBehind || isAhead)
                    {
                        if (!_alertCooldowns.TryGetValue(lagKey, out var lastAlert) || (DateTime.UtcNow - lastAlert).TotalMinutes >= 3)
                        {
                            _alertCooldowns[lagKey] = DateTime.UtcNow;

                            string distText = lagDistance > 1000 ? $"{Math.Round(lagDistance / 1000.0, 1)} kilometers" : $"{Math.Round(lagDistance)} meters";
                            string statusText = isAhead ? "ahead of" : "behind";
                            string broadcastMessage = session.CurrentState == Shared.Constants.GroupState.Navigating
                                ? $"{userName} is {distText} {statusText} the Lead."
                                : $"{userName} is separated from the Lead by {distText}.";

                            bool isLeadership = currentRider.GoogleId == session.AdminGoogleId ||
                                                currentRider.Role == "Lead" ||
                                                currentRider.Role == "Marshal" ||
                                                currentRider.Role == "Tail";

                            // Send to Leadership (Excluding the rider themselves)
                            var leadershipConns = _state.ConnectedRiders.Values
                                .Where(r => r.GroupName == groupName && !string.IsNullOrEmpty(r.ConnectionId) &&
                                            r.GoogleId != currentRider.GoogleId &&
                                           (r.GoogleId == session.AdminGoogleId || r.Role == "Lead" || r.Role == "Marshal" || r.Role == "Tail"))
                                .Select(r => r.ConnectionId).ToList();

                            foreach (var connId in leadershipConns)
                            {
                                await Clients.Client(connId).SendAsync("ReceiveAlert", "VoicePrompt", broadcastMessage);
                            }

                            // Warn the standard rider directly
                            if (!isLeadership)
                            {
                                string directMessage = isAhead
                                    ? $"Warning: You are {distText} ahead of the Lead. Please fall back."
                                    : $"Warning: You are {distText} behind the Lead.";
                                await Clients.Client(currentRider.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", directMessage);
                            }
                        }
                    }
                    else
                    {
                        _alertCooldowns.TryRemove(lagKey, out _);
                    }
                }
            }
            else
            {
                // This is the Lead Rider - Calculate Splinter (Overall Spread of the Group)
                double maxDist = 0;
                foreach (var r in _state.ConnectedRiders.Values.Where(x => x.GroupName == groupName && x.GoogleId != leadGoogleId && x.LastLat != 0))
                {
                    double d = CalculateDistanceMeters(lat, lng, r.LastLat, r.LastLng);
                    if (d > maxDist) maxDist = d;
                }

                string splinterKey = $"splinter_{groupName}";

                // Only Splinter warn if enabled (> 0)
                if (session.Settings.SplinterWarningDistanceMeters > 0 && maxDist > session.Settings.SplinterWarningDistanceMeters)
                {
                    if (!_alertCooldowns.TryGetValue(splinterKey, out var lastAlert) || (DateTime.UtcNow - lastAlert).TotalMinutes >= 5)
                    {
                        _alertCooldowns[splinterKey] = DateTime.UtcNow;
                        await Clients.Client(currentRider.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", "Convoy splintered! The group is stretched too far.");
                    }
                }
                else { _alertCooldowns.TryRemove(splinterKey, out _); }

                // Arrival Detection
                if (session.CurrentState == Shared.Constants.GroupState.Navigating)
                {
                    double distToDest = CalculateDistanceMeters(lat, lng, session.DestLat, session.DestLng);
                    if (distToDest < (session.Settings.ArrivalGeofenceMeters > 0 ? session.Settings.ArrivalGeofenceMeters : 1000))
                    {
                        string arrivalKey = $"arrival_{groupName}";
                        if (!_alertCooldowns.ContainsKey(arrivalKey))
                        {
                            _alertCooldowns[arrivalKey] = DateTime.UtcNow;
                            await Clients.Group(groupName).SendAsync("ReceiveAlert", "VoicePrompt", $"The Lead is arriving at the destination.");
                        }
                    }
                }
            }
        }
    }
    // --- NEW: LIVE TELEMETRY DASHBOARD ENDPOINT ---
    public async Task<List<TelemetryDto>> GetGroupTelemetry(string groupName)
    {
        var result = new List<TelemetryDto>();
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        if (session == null) return result;

        string leadGoogleId = string.IsNullOrEmpty(session.Settings.LeadRiderGoogleId) ? session.AdminGoogleId : session.Settings.LeadRiderGoogleId;
        var leadRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GoogleId == leadGoogleId);

        foreach (var rider in _state.ConnectedRiders.Values.Where(r => r.GroupName == groupName))
        {
            double speed = _telemetryStats.TryGetValue(rider.GoogleId, out var stats) ? stats.CurrentSpeedKmh : 0;
            double lagDistance = 0;
            string status = "In Formation";

            if (leadRider != null && rider.GoogleId != leadGoogleId && rider.LastLat != 0 && leadRider.LastLat != 0)
            {
                if (session.CurrentState == Shared.Constants.GroupState.Navigating)
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

            result.Add(new TelemetryDto { Name = rider.UserName, SpeedKmh = speed, DistanceMeters = lagDistance, Status = status });
        }

        return result.OrderBy(r => r.Status == "Behind").ThenByDescending(r => r.DistanceMeters).ToList();
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
            if (session.CurrentState == Shared.Constants.GroupState.Navigating)
                await Clients.Caller.SendAsync("NavigationStarted", session.DestLat, session.DestLng, session.DestName, true);
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
        var caller = _state.ConnectedRiders.Values.FirstOrDefault(r => r.ConnectionId == Context.ConnectionId);
        var session = await _state.ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();

        if (caller != null && session != null && session.AdminGoogleId == caller.GoogleId)
        {
            // --- NEW: Enforce unique roles (Only ONE Lead, Tail, or Marshal per group) ---
            if (newRole == "Lead" || newRole == "Tail" || newRole == "Marshal")
            {
                // Find anyone else who currently has this exact role and demote them
                var existingHolders = _state.ConnectedRiders.Values
                    .Where(r => r.GroupName == groupName && r.Role == newRole && r.GoogleId != targetGoogleId)
                    .ToList();

                foreach (var oldHolder in existingHolders)
                {
                    oldHolder.Role = "Rider";

                    // Voice prompt to let the previous holder know they were demoted
                    if (!string.IsNullOrEmpty(oldHolder.ConnectionId))
                    {
                        await Clients.Client(oldHolder.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"Your role has been reassigned. You are now a Standard Rider.");
                    }
                }
            }

            // Assign the new role to the target rider
            var targetRider = _state.ConnectedRiders.Values.FirstOrDefault(r => r.GroupName == groupName && r.GoogleId == targetGoogleId);
            if (targetRider != null)
            {
                targetRider.Role = newRole;

                // CRITICAL: If a new Lead is assigned, update the Database so the Telemetry Engine knows who to track!
                if (newRole == "Lead")
                {
                    var update = Builders<GroupSession>.Update.Set(g => g.Settings.LeadRiderGoogleId, targetGoogleId);
                    await _state.ActiveGroups.UpdateOneAsync(g => g.GroupName == groupName, update);
                }

                // Voice prompt to let the newly promoted user know!
                if (!string.IsNullOrEmpty(targetRider.ConnectionId) && targetRider.GoogleId != caller.GoogleId)
                {
                    await Clients.Client(targetRider.ConnectionId).SendAsync("ReceiveAlert", "VoicePrompt", $"You have been designated as the {newRole}.");
                }

                // Broadcast the visually updated roster to everyone
                await Clients.Group(groupName).SendAsync("RosterUpdated", await GetGroupRoster(groupName));
            }
        }
    }
    // --- NEW: PAUSE, RESUME, AND COMPLETE ---

    public async Task PauseNavigation(string groupName, string reason, string adminName)
    {
        // Map the string reason from the frontend UI to the exact Backend Enum
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
}