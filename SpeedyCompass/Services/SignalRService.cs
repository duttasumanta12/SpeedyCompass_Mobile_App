using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SpeedyCompass.Models;
using System.Net.Http;
using System.Net.Security;
#if ANDROID
using static Android.Provider.Settings;
#endif

namespace SpeedyCompass.Services;

public class SignalRService
{
    private readonly HubConnection _hubConnection;

    // Standard C# events that UI pages can subscribe to
    public event Action<List<Rider>> RosterUpdated;
    public event Action<double, double, string> NavigationStarted;
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

    public SignalRService()
    {
        try
        {
            // Switch to HTTPS and standard ASP.NET Core HTTPS ports (e.g., 5001 or 7001)
            // Note: Check your backend's launchSettings.json to ensure the https port is correct
            string baseUrl = DeviceInfo.Platform == DevicePlatform.Android
            ? "https://10.0.2.2:7219" //"https://speedycompassbe-dme4f2hncnb0e4ad.southcentralus-01.azurewebsites.net/"  // Android emulator maps 10.0.2.2 to the host machine
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

        _hubConnection.On<double, double, string>("NavigationStarted", (lat, lng, name) =>
        {
            NavigationStarted?.Invoke(lat, lng, name);
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
            ConnectionStatusChanged?.Invoke("Connected", Colors.Green);
            try
            {
                // STANDARD RESTORE: If SignalR auto-reconnected quickly
                if (!string.IsNullOrEmpty(_activeGoogleId) && !string.IsNullOrEmpty(_activeGroupName))
                {
                    await _hubConnection.InvokeAsync("RestoreConnectionState", _activeGoogleId, _activeUserName, _activeGroupName);
                }
            }
            catch (Exception ex) { LogException("Reconnected State Sync", ex); }
        };

        // NEW PTT LISTENERS
        _hubConnection.On<string>("PttLocked", (speakerName) => PttLocked?.Invoke(speakerName));
        _hubConnection.On<string>("PttDenied", (activeSpeaker) => PttDenied?.Invoke(activeSpeaker));
        _hubConnection.On("PttReleased", () => PttReleased?.Invoke());
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
    public async Task<string?> AuthenticateUser(string googleId)
    {
        try
        {
            return await _hubConnection.InvokeAsync<string?>("AuthenticateUser", googleId);
        }
        catch (Exception ex)
        {
            LogException(nameof(AuthenticateUser), ex);
            return null;
        }
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
    public async Task UpdateGroupSettings(string groupName, int maxLag, int splinterDistance)
    {
        try
        {
            await _hubConnection.InvokeAsync("UpdateGroupSettings", groupName, maxLag, splinterDistance);
        }
        catch (Exception ex)
        {
            LogException(nameof(UpdateGroupSettings), ex);
        }
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

    public async Task CreateGroup(string groupName, string userName)
    {
        try
        {
            await _hubConnection.InvokeAsync("CreateGroup", groupName, userName);
        }
        catch (Exception ex)
        {
            LogException(nameof(CreateGroup), ex);
            throw; // Let the UI handle alerting the user 
        }
    }

    public async Task JoinGroup(string groupName, string userName)
    {
        try
        {
            await _hubConnection.InvokeAsync("JoinGroup", groupName, userName);
        }
        catch (Exception ex)
        {
            LogException(nameof(JoinGroup), ex);
            throw; // Let the UI handle alerting the user 
        }
    }

    public async Task StartGroupNavigation(string groupName, double lat, double lng, string destName)
    {
        try
        {
            await _hubConnection.InvokeAsync("StartNavigation", groupName, lat, lng, destName);
        }
        catch (Exception ex)
        {
            LogException(nameof(StartGroupNavigation), ex);
        }
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

    public async Task UpdateLocation(string groupName, string userName, double lat, double lng, double heading)
    {
        try
        {
            await _hubConnection.InvokeAsync("UpdateMyLocation", groupName, userName, lat, lng, heading);
        }
        catch (Exception ex)
        {
            LogException(nameof(UpdateLocation), ex);
        }
    }
    public async Task SendGroupAlert(string groupName, string alertType, string senderName)
    {
        try
        {
            await _hubConnection.InvokeAsync("SendGroupAlert", groupName, alertType, senderName);
        }
        catch (Exception ex)
        {
            LogException(nameof(SendGroupAlert), ex);
        }
    }
    public async Task<string> RegisterOrUpdateUser(string googleId, string desiredUsername)
    {
        // We DO NOT catch exceptions here because we want the HubException ("Username is already taken") 
        // to bubble up to the MainPage so we can show a DisplayAlert to the user!
        return await _hubConnection.InvokeAsync<string>("RegisterOrUpdateUser", googleId, desiredUsername);
    }
    public async Task SetGroupDestination(string groupName, double lat, double lng, string destName)
    {
        try
        {
            await _hubConnection.InvokeAsync("SetDestination", groupName, lat, lng, destName);
        }
        catch (Exception ex)
        {
            LogException(nameof(SetGroupDestination), ex);
        }
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

    public async Task JoinGroup(string groupName, string userName, string googleId)
    {
        _activeGroupName = groupName;
        _activeUserName = userName;
        _activeGoogleId = googleId;
        await _hubConnection.InvokeAsync("JoinGroup", groupName, userName, googleId);
    }

    public async Task LeaveGroup()
    {
        // Clear state so we don't try to reconnect to a group we left
        _activeGroupName = string.Empty;
        await _hubConnection.InvokeAsync("LeaveGroup");
    }

    public async Task DeleteGroup(string groupName) => await _hubConnection.InvokeAsync("DeleteGroup", groupName);

    // Global Console Logger Helper
    private void LogException(string context, Exception ex)
    {
        // Standard console output
        Console.WriteLine($"[SignalR Exception] {context}: {ex.Message}");

        // Ensure it appears in the MAUI Debug output window
        System.Diagnostics.Debug.WriteLine($"[SignalR Exception] {context}: {ex}");
    }

}