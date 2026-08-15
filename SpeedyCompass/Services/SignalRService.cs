using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SpeedyCompass.Models;
using SpeedyCompass.Shared.Models;
using System.Collections.Concurrent;
using System.Net.Security;
using SQLite;
using System.Text.Json;

#if ANDROID
using static Android.Provider.Settings;
#endif

namespace SpeedyCompass.Services;
// --- 1. THE OUTBOX COMMAND MODEL ---


// --- 1. THE SQLITE OUTBOX MODEL ---
public class OutboxCommandEntity
{
    [PrimaryKey]
    public string DedupeKey { get; set; }
    public string MethodName { get; set; }

    // We store the arguments and their exact C# Types so SignalR 
    // doesn't get confused by generic JSON Elements when we deserialize!
    public string ArgsJsonArray { get; set; }
    public string ArgTypesJsonArray { get; set; }

    public DateTime CreatedUtc { get; set; }
}

public class SignalRService
{
    private readonly HubConnection _hubConnection;
    private SQLiteAsyncConnection _db;
    private bool _isFlushing = false;

    private async Task InitOutboxDbAsync()
    {
        if (_db != null) return;

        string dbPath = Path.Combine(FileSystem.AppDataDirectory, "outbox.db3");
        _db = new SQLiteAsyncConnection(dbPath);
        await _db.CreateTableAsync<OutboxCommandEntity>();
    }

    // Standard C# events that UI pages can subscribe to
    public event Action<List<Rider>> RosterUpdated;
    public event Action<double, double, string, bool> NavigationStarted;
    public event Action NavigationCancelled; // NEW: Event for when navigation is stopped
    public event Action<string, Color> ConnectionStatusChanged;
    public event Action<string, double, double, double> RiderLocationUpdated;
    public event Action<string, string> AlertReceived;
    // 1. Add this new event near the top of your class
    public event Action<double, double, string> DestinationSet;
    // Inside SignalRService class, add these events:
    public event Action<string> UserJoinedAlert;
    public event Action<string> UserLeftAlert;
    public event Action GroupDeleted;
    // --- NEW PTT EVENTS ---
    public event Action<string> PttLocked;
    public event Action<string> PttDenied;
    public event Action PttReleased;
    public event Action<string, string> NavigationPaused; // Reason, AdminName
    public event Action<string> NavigationResumed; // AdminName
    public event Action<string> NavigationCompleted; // AdminName
    public event Action<string> LeadRouteUpdated;
    public event Action<string> RouteDeviationAlert;
    public event Action<double, double> MeetupPointSet;
    // ==========================================================
    // --- NEW: DYNAMIC ROUTING, MEETUPS & SETTINGS EVENTS ---
    // ==========================================================
    public event Action<GroupSettingsDto> GroupSettingsUpdated;
    // Add this state flag near the top of your SignalRService class
    private bool _isBackgroundListenerMode = false;
    public event Action<string, bool> VisibilityToggleReceived;

    public SignalRService()
    {
        try
        {
            // Switch to HTTPS and standard ASP.NET Core HTTPS ports (e.g., 5001 or 7001)
            // Note: Check your backend's launchSettings.json to ensure the https port is correct
            string baseUrl = DeviceInfo.Platform == DevicePlatform.Android
            ? //"https://10.0.2.2:7219" 
             "https://speedycompassbe-dme4f2hncnb0e4ad.southcentralus-01.azurewebsites.net/"  // Android emulator maps 10.0.2.2 to the host machine
                : "https://localhost:5001"; // iOS Simulator and Windows/Mac use standard localhost

            // IMPORTANT: If testing on PHYSICAL devices on your local Wi-Fi, 
            // you must hardcode your host machine's local IP address instead:
            // baseUrl = "https://192.168.1.X:5001"; 

            _hubConnection = new HubConnectionBuilder()
                .WithUrl($"{baseUrl}/compasshub", options =>
                {
                    // Explicitly allow WebSockets with a fallback to Long Polling
                    options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;

                    // Configure custom HttpClientHandler to bypass SSL certificate validation 
                    // for local development across ALL MAUI native platforms
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                        {
                            clientHandler.ServerCertificateCustomValidationCallback =
                                (message, cert, chain, errors) => { return true; };
                        }
                        else if (handler is SocketsHttpHandler socketsHandler)
                        {
                            socketsHandler.SslOptions = new SslClientAuthenticationOptions
                            {
                                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                            };
                        }
#if ANDROID
                        else if (handler is Xamarin.Android.Net.AndroidMessageHandler androidHandler)
                        {
                            androidHandler.ServerCertificateCustomValidationCallback =
                                (message, cert, chain, errors) => { return true; };
                        }
#endif
#if IOS || MACCATALYST
                        //else if (handler is Foundation.NSUrlSessionHandler iosHandler)
                        //{
                        //    iosHandler.TrustOverrideForUrl = 
                        //        (sender, url, trust) => { return true; };
                        //}
#endif
                        return handler;
                    };
                })
                .WithAutomaticReconnect(new[]
                { 
                    // Attempt to reconnect immediately, then after 2s, 5s, and 10s
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10)
                })
                .ConfigureLogging(logging =>
                {
                    // Output internal SignalR logs to the VS Debug Console
                    logging.SetMinimumLevel(LogLevel.Debug);
                    logging.AddDebug();
                })
                .Build();

            RegisterHubListeners();
        }
        catch (Exception ex)
        {
            LogException("Initialization", ex);
            throw; // Re-throw so the app is aware the service failed to initialize
        }
    }
    public event Action<string> UserOfflineAlert;
    /// <summary>
    /// Replaces direct _hubConnection.InvokeAsync. Safely saves to SQLite if offline.
    /// </summary>
    private async Task SendOrQueueAsync(string methodName, string dedupeKey, params object[] args)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeCoreAsync(methodName, args);

                // If it successfully sent, make sure we clean up any old queued version
                await InitOutboxDbAsync();
                await _db.DeleteAsync<OutboxCommandEntity>(dedupeKey);
                return;
            }
            catch (Exception ex)
            {
                LogException($"Direct Send Failed: {methodName}", ex);
            }
        }

        // --- OFFLINE: Persist to SQLite ---
        await InitOutboxDbAsync();

        // Safely serialize the arguments and their types for perfect reconstruction later
        string[] types = args.Select(a => a.GetType().AssemblyQualifiedName).ToArray();
        string[] serializedArgs = args.Select(a => JsonSerializer.Serialize(a)).ToArray();

        var entity = new OutboxCommandEntity
        {
            DedupeKey = dedupeKey,
            MethodName = methodName,
            ArgsJsonArray = JsonSerializer.Serialize(serializedArgs),
            ArgTypesJsonArray = JsonSerializer.Serialize(types),
            CreatedUtc = DateTime.UtcNow
        };

        // InsertOrReplace ensures our DedupeKey logic works! (e.g. overwriting stale GPS pings)
        await _db.InsertOrReplaceAsync(entity);
        System.Diagnostics.Debug.WriteLine($"[Outbox DB] Saved {methodName} under {dedupeKey}");
    }

    /// <summary>
    /// Called automatically upon reconnection to flush the SQLite database.
    /// </summary>
    private async Task FlushOutboxAsync()
    {
        if (_isFlushing || _hubConnection.State != HubConnectionState.Connected) return;

        await InitOutboxDbAsync();
        _isFlushing = true;

        try
        {
            // Pull all pending commands, oldest first
            var pendingCommands = await _db.Table<OutboxCommandEntity>().OrderBy(x => x.CreatedUtc).ToListAsync();

            if (!pendingCommands.Any()) return;
            System.Diagnostics.Debug.WriteLine($"[Outbox DB] Flushing {pendingCommands.Count} pending commands...");

            foreach (var cmd in pendingCommands)
            {
                if (_hubConnection.State != HubConnectionState.Connected) break; // Lost connection mid-flush

                try
                {
                    // 1. Carefully reconstruct the strongly-typed arguments
                    var typesList = JsonSerializer.Deserialize<string[]>(cmd.ArgTypesJsonArray);
                    var argsList = JsonSerializer.Deserialize<string[]>(cmd.ArgsJsonArray);

                    object[] reconstructedArgs = new object[argsList.Length];
                    for (int i = 0; i < argsList.Length; i++)
                    {
                        Type t = Type.GetType(typesList[i]);
                        reconstructedArgs[i] = JsonSerializer.Deserialize(argsList[i], t);
                    }

                    // 2. Fire it at the server
                    await _hubConnection.InvokeCoreAsync(cmd.MethodName, reconstructedArgs);

                    // 3. Delete from DB only after successful transmission
                    await _db.DeleteAsync(cmd);
                    System.Diagnostics.Debug.WriteLine($"[Outbox DB] Successfully flushed {cmd.MethodName}");
                }
                catch (Exception ex)
                {
                    LogException($"Outbox Flush Failed: {cmd.MethodName}", ex);
                    // We break the loop so we don't send commands out of order
                    break;
                }
            }
        }
        finally
        {
            _isFlushing = false;
        }
    }

    private void RegisterHubListeners()
    {
        _hubConnection.On<string>("UserOfflineAlert", (userName) => UserOfflineAlert?.Invoke(userName));
        _hubConnection.On<string>("UserJoinedAlert", (userName) => UserJoinedAlert?.Invoke(userName));
        _hubConnection.On<string>("UserLeftAlert", (userName) => UserLeftAlert?.Invoke(userName));
        _hubConnection.On("GroupDeleted", () => GroupDeleted?.Invoke());
        // 1. Map raw SignalR string events to strongly-typed C# events
        _hubConnection.On<List<Rider>>("RosterUpdated", (roster) =>
        {
            RosterUpdated?.Invoke(roster);
        });

        _hubConnection.On<double, double, string, bool>("NavigationStarted", (lat, lng, name, isSyncRequired) =>
        {
            NavigationStarted?.Invoke(lat, lng, name, isSyncRequired);
        });

        // NEW: Listen for the navigation cancelled broadcast
        _hubConnection.On("NavigationCancelled", () =>
        {
            NavigationCancelled?.Invoke();
        });

        _hubConnection.On<string, double, double, double>("ReceiveRiderLocation", (riderId, lat, lng, heading) =>
        {
            RiderLocationUpdated?.Invoke(riderId, lat, lng, heading);
        });

        _hubConnection.On<string, string>("ReceiveAlert", (alertType, senderName) =>
        {
            AlertReceived?.Invoke(alertType, senderName);
        });

        _hubConnection.On<double, double, string>("DestinationSet", (lat, lng, name) =>
        {
            DestinationSet?.Invoke(lat, lng, name);
        });

        // --- RECONNECTION LOGIC ---
        _hubConnection.Closed += async (error) =>
        {
            if (error == null) return;
            ConnectionStatusChanged?.Invoke("Disconnected", Colors.Red);
            if (error != null) LogException("Connection Closed", error);

            // AGGRESSIVE FALLBACK: Keep trying if SignalR's internal 10-second retry fails
            while (_hubConnection.State == HubConnectionState.Disconnected && !string.IsNullOrEmpty(_activeGroupName))
            {
                try
                {
                    await Task.Delay(5000);
                    await StartAsync();

                    if (_hubConnection.State == HubConnectionState.Connected && !string.IsNullOrEmpty(_activeGoogleId))
                    {
                        await _hubConnection.InvokeAsync("RestoreConnectionState", _activeGoogleId, _activeUserName, _activeGroupName);
                        ConnectionStatusChanged?.Invoke("Connected", Colors.Green);
                    }
                }
                catch { /* Quietly loop until cell tower is found */ }
            }
        };

        _hubConnection.Reconnecting += async (error) =>
        {
            ConnectionStatusChanged?.Invoke("Reconnecting...", Colors.Orange);
            if (error != null) LogException("Reconnecting", error);
        };

        _hubConnection.Reconnected += async (connectionId) =>
        {
            ConnectionStatusChanged?.Invoke("Connected", Colors.MediumSeaGreen);
            try
            {
                if (!string.IsNullOrEmpty(_activeGoogleId) && !string.IsNullOrEmpty(_activeGroupName))
                {
                    await _hubConnection.InvokeAsync("RestoreConnectionState", _activeGoogleId, _activeUserName, _activeGroupName);
                    ConnectionStatusChanged?.Invoke("Connected", Colors.Green);

                    // THE FIX: Flush the queue!
                    _ = FlushOutboxAsync();
                }
            }
            catch (Exception ex) { LogException("Reconnected State Sync", ex); }
        };

        // NEW PTT LISTENERS
        _hubConnection.On<string>("PttLocked", (speakerName) => PttLocked?.Invoke(speakerName));
        _hubConnection.On<string>("PttDenied", (activeSpeaker) => PttDenied?.Invoke(activeSpeaker));
        _hubConnection.On("PttReleased", () => PttReleased?.Invoke());
        _hubConnection.On<string, string>("ReceiveNavigationPaused", (reason, adminName) => NavigationPaused?.Invoke(reason, adminName));
        _hubConnection.On<string>("ReceiveNavigationResumed", (adminName) => NavigationResumed?.Invoke(adminName));
        _hubConnection.On<string>("ReceiveNavigationCompleted", (adminName) => NavigationCompleted?.Invoke(adminName));
        _hubConnection.On<string>("ReceiveNewLeadRoute", (poly) => LeadRouteUpdated?.Invoke(poly));
        _hubConnection.On<string>("ReceiveRouteDeviation", (user) => RouteDeviationAlert?.Invoke(user));
        _hubConnection.On<double, double>("ReceiveMeetupPoint", (lat, lng) => MeetupPointSet?.Invoke(lat, lng));
        _hubConnection.On<GroupSettingsDto>("ReceiveGroupSettings", (settings) => GroupSettingsUpdated?.Invoke(settings));
        _hubConnection.On<string, double, double, double>("UpdateRiderLocation", (riderId, lat, lng, heading) =>
        {
            // =====================================================================
            // THE CPU SHIELD: If Perspective #2 (Gmaps mode) is active, silently drop 
            // incoming heavy data to save RAM and Battery!
            // =====================================================================
            if (_isBackgroundListenerMode) return;

            RiderLocationUpdated?.Invoke(riderId, lat, lng, heading);
        });
        _hubConnection.On<string, bool>("ReceiveVisibilityToggle", (callerName, hide) =>
        {
            VisibilityToggleReceived?.Invoke(callerName, hide);
        });
    }

    // Explicit Hub Commands with Global Exception Handling
    public async Task StartAsync()
    {
        // NEW: Only start if disconnected, allowing safe multiple calls from OnAppearing
        if (_hubConnection.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _hubConnection.StartAsync();
                await FlushOutboxAsync();
            }
            catch (Exception ex)
            {
                LogException(nameof(StartAsync), ex);
            }
        }
    }
    // --- NEW PTT METHODS ---
    public async Task RequestPtt(string groupName, string userName)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("RequestPtt", groupName, userName);
    }

    public async Task ReleasePtt(string groupName, string userName)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("ReleasePtt", groupName, userName);
    }
    // --- NEW: AUTHENTICATION WRAPPERS ---
    // UPDATED: Return the full profile DTO
    public async Task<UserProfileDto?> AuthenticateUser(string googleId)
    {
        try { return await _hubConnection.InvokeAsync<UserProfileDto>("AuthenticateUser", googleId); }
        catch (Exception ex) { LogException(nameof(AuthenticateUser), ex); return null; }
    }
    public async Task<bool> SaveUserProfile(string googleId, UserProfileDto profile)
    {
        try { return await _hubConnection.InvokeAsync<bool>("SaveUserProfile", googleId, profile); }
        catch (Exception ex) { LogException(nameof(SaveUserProfile), ex); return false; }
    }
    // NEW: Fetch Emergency Info
    public async Task<UserProfileDto?> GetRiderEmergencyInfo(string targetGoogleId)
    {
        try { return await _hubConnection.InvokeAsync<UserProfileDto>("GetRiderEmergencyInfo", targetGoogleId); }
        catch (Exception ex) { LogException(nameof(GetRiderEmergencyInfo), ex); return null; }
    }
    // UPDATE: Add maxGroupSize to the parameter list
    public async Task UpdateGroupSettings(string groupName, int maxLag, int splinterDistance, int maxGroupSize)
    {
        try { await _hubConnection.InvokeAsync("UpdateGroupSettings", groupName, maxLag, splinterDistance, maxGroupSize); }
        catch (Exception ex) { LogException(nameof(UpdateGroupSettings), ex); }
    }
    // NEW: Call the hub to assign a role
    public async Task AssignRole(string groupName, string targetGoogleId, string role)
    {
        try { await _hubConnection.InvokeAsync("AssignRole", groupName, targetGoogleId, role); }
        catch (Exception ex) { LogException(nameof(AssignRole), ex); }
    }
    // UPDATE: Add pitstopDist to the parameter list
    public async Task UpdateGroupSettings(string groupName, GroupSettingsDto settings)
    {
        try { await _hubConnection.InvokeAsync("UpdateGroupSettings", groupName, settings); }
        catch (Exception ex) { LogException(nameof(UpdateGroupSettings), ex); }
    }

    public async Task<bool> CheckGroupExists(string groupName)
    {
        try
        {
            return await _hubConnection.InvokeAsync<bool>("CheckGroupExists", groupName);
        }
        catch (Exception ex)
        {
            LogException(nameof(CheckGroupExists), ex);
            return false;
        }
    }

    public async Task CreateGroup(string groupName, string userName, string googleId, string joinCode, GroupSettingsDto initialSettings)
    {
        try
        {
            await _hubConnection.InvokeAsync("CreateGroup", groupName, userName, googleId, joinCode, initialSettings);
        }
        catch (Exception ex)
        {
            LogException(nameof(CreateGroup), ex);
            throw; // Let the UI handle alerting the user 
        }
    }
    // --- NEW: Fetch group details ---
    public async Task<GroupDetailsDto> GetGroupDetails(string groupName)
    {
        if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
        {
            try
            {
                return await _hubConnection.InvokeAsync<GroupDetailsDto>("GetGroupDetails", groupName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching group details: {ex.Message}");
            }
        }
        return null;
    }
    public async Task StartGroupNavigation(string groupName, double lat, double lng, string destName)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await SendOrQueueAsync("StartNavigation", operationId, groupName, lat, lng, destName, operationId);
    }
    public async Task<List<Rider>> GetGroupRoster(string groupName)
    {
        try
        {
            // Ask the server for the current roster on demand!
            return await _hubConnection.InvokeAsync<List<Rider>>("GetGroupRoster", groupName);
        }
        catch (Exception ex)
        {
            LogException(nameof(GetGroupRoster), ex);
            return new List<Rider>();
        }
    }
    public async Task LeaveLobby()
    {
        try
        {
            // Tell the server we are stepping back to the MainPage
            await _hubConnection.InvokeAsync("LeaveLobby");
        }
        catch (Exception ex)
        {
            LogException(nameof(LeaveLobby), ex);
        }
    }

    // NEW: Client command to reset the group's navigation
    public async Task CancelGroupNavigation(string groupName)
    {
        try
        {
            await _hubConnection.InvokeAsync("CancelNavigation", groupName);
        }
        catch (Exception ex)
        {
            LogException(nameof(CancelGroupNavigation), ex);
        }
    }

    public async Task UpdateLocation(string groupName, string userName, double lat, double lng, double heading, List<string> excludedUsers)
    {
        // DEDUPE KEY: "LocationUpdate". 
        // If offline for 20 mins, we only queue the LATEST coordinate!
        await SendOrQueueAsync("UpdateMyLocation", "LocationUpdate", groupName, userName, lat, lng, heading, excludedUsers);
    }
    // ==========================================================
    // --- NEW: PEER-TO-PEER VISIBILITY TOGGLE ---
    // ==========================================================
    public async Task SendVisibilityToggle(string groupName, string targetUserName, bool hide)
    {
        // We only send this if connected. If offline, there's no location to hide anyway!
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("SendVisibilityToggleToRider", groupName, targetUserName, hide);
            }
            catch (Exception ex)
            {
                LogException(nameof(SendVisibilityToggle), ex);
            }
        }
    }
    public async Task<List<Models.TelemetryDto>> GetGroupTelemetry(string groupName)
    {
        if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
        {
            try
            {
                return await _hubConnection.InvokeAsync<List<Models.TelemetryDto>>("GetGroupTelemetry", groupName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching telemetry: {ex.Message}");
            }
        }
        return new List<Models.TelemetryDto>(); // Return empty list if disconnected
    }
    // 4. NEW: Call the hub to fetch the settings
    public async Task<GroupSettingsDto> GetGroupSettings(string groupName)
    {
        try { return await _hubConnection.InvokeAsync<GroupSettingsDto>("GetGroupSettings", groupName); }
        catch (Exception ex)
        {
            LogException(nameof(GetGroupSettings), ex);
            return null;
        }
    }
    public async Task SendGroupAlert(string groupName, string alertType, string senderName)
    {
        // DEDUPE KEY: Guid. We want every single alert to go through sequentially, no overwriting!
        await SendOrQueueAsync("SendGroupAlert", Guid.NewGuid().ToString(), groupName, alertType, senderName);
    }
    public async Task<string> RegisterOrUpdateUser(string googleId, string desiredUsername)
    {
        // We DO NOT catch exceptions here because we want the HubException ("Username is already taken") 
        // to bubble up to the MainPage so we can show a DisplayAlert to the user!
        return await _hubConnection.InvokeAsync<string>("RegisterOrUpdateUser", googleId, desiredUsername);
    }
    public async Task SetGroupDestination(string groupName, double lat, double lng, string destName)
    {
        await SendOrQueueAsync("SetDestination", "DestChange", groupName, lat, lng, destName);
    }
    private string _activeGoogleId = string.Empty;
    private string _activeUserName = string.Empty;
    private string _activeGroupName = string.Empty;

    // Inside SignalRService, update Create and Join and add new wrappers:
    public async Task<List<ActiveGroupDto>> GetActiveGroups()
        => await _hubConnection.InvokeAsync<List<ActiveGroupDto>>("GetActiveGroups");

    // 1. Update Create and Join to capture the state
    public async Task CreateGroup(string groupName, string userName, string googleId)
    {
        _activeGroupName = groupName;
        _activeUserName = userName;
        _activeGoogleId = googleId;
        await _hubConnection.InvokeAsync("CreateGroup", groupName, userName, googleId);
    }

    public async Task JoinGroup(string groupName, string userName, string googleId, string pinCode)
    {
        _activeGroupName = groupName;
        _activeUserName = userName;
        _activeGoogleId = googleId;
        await _hubConnection.InvokeAsync("JoinGroup", groupName, userName, googleId, pinCode);
    }

    // UPDATE: Pass googleId to the backend
    public async Task LeaveGroup(string googleId)
    {
        try { await _hubConnection.InvokeAsync("LeaveGroup", googleId); }
        catch (Exception ex) { LogException(nameof(LeaveGroup), ex); }
    }

    // UPDATE: Pass googleId to the backend
    public async Task DeleteGroup(string groupName, string googleId)
    {
        try { await _hubConnection.InvokeAsync("DeleteGroup", groupName, googleId); }
        catch (Exception ex) { LogException(nameof(DeleteGroup), ex); }
    }

    // Global Console Logger Helper
    private void LogException(string context, Exception ex)
    {
        // Standard console output
        Console.WriteLine($"[SignalR Exception] {context}: {ex.Message}");

        // Ensure it appears in the MAUI Debug output window
        System.Diagnostics.Debug.WriteLine($"[SignalR Exception] {context}: {ex}");
    }

    internal async Task StopAsync()
    {
        try
        {
            if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
            {
                await _hubConnection.StopAsync();
            }
        }
        catch (Exception ex)
        {
            LogException(nameof(StopAsync), ex);
        }
    }
    public async Task PauseGroupNavigation(string groupName, string reason, string adminName)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await SendOrQueueAsync("PauseNavigation", operationId, groupName, reason, adminName, operationId);
    }

    public async Task ResumeGroupNavigation(string groupName, string adminName)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await SendOrQueueAsync("ResumeNavigation", operationId, groupName, adminName, operationId);
    }

    public async Task CompleteGroupNavigation(string groupName, string adminName)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await SendOrQueueAsync("CompleteNavigation", operationId, groupName, adminName, operationId);
    }
    // --- NEW: DYNAMIC ROUTING & MEETUPS ---
    public async Task BroadcastLeadRoute(string groupName, string encodedPolyline)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("UpdateGroupRoute", groupName, encodedPolyline);
    }

    public async Task ReportRouteDeviation(string groupName, string userName)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("NotifyRouteDeviation", groupName, userName);
    }

    public async Task SetGroupMeetupPoint(string groupName, double lat, double lng)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("SetMeetupPoint", groupName, lat, lng);
    }
    // ==========================================================
    // --- NEW: EDGE TELEMETRY TRIGGERS ---
    // ==========================================================
    public async Task SendLagWarning(string groupName, string userName, double distanceMeters, bool isAhead)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("RelayLagWarning", groupName, userName, distanceMeters, isAhead);
    }

    public async Task SendSplinterWarning(string groupName)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("RelaySplinterWarning", groupName);
    }

    public async Task SendPitstopReminder(string groupName, double distanceKm)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("RelayPitstopReminder", groupName, distanceKm);
    }

    public async Task SendArrivalAlert(string groupName)
    {
        if (_hubConnection.State == HubConnectionState.Connected)
            await _hubConnection.InvokeAsync("RelayArrivalAlert", groupName);
    }
    public async Task ToggleBackgroundListenerMode(string groupName, bool isBackgroundMode)
    {
        _isBackgroundListenerMode = isBackgroundMode;

        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                // OPTIONAL: If you add this method to your backend C# Hub, it will stop the 
                // server from even sending the data to this specific user, saving network bandwidth.
                // If the backend method doesn't exist yet, this just silently fails and 
                // relies on the local CPU shield below.
                await _hubConnection.InvokeAsync("ToggleBackgroundListener", groupName, isBackgroundMode);
            }
            catch
            {
                // Swallow exception if backend Hub doesn't have this method yet.
            }
        }
    }
}