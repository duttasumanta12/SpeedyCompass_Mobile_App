using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Connections;
using System.Net.Http;
using System.Net.Security;

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

    public SignalRService()
    {
        try
        {
            // Switch to HTTPS and standard ASP.NET Core HTTPS ports (e.g., 5001 or 7001)
            // Note: Check your backend's launchSettings.json to ensure the https port is correct
            string baseUrl = DeviceInfo.Platform == DevicePlatform.Android
            ? "https://speedycompassbe-dme4f2hncnb0e4ad.southcentralus-01.azurewebsites.net/"  // Android emulator maps 10.0.2.2 to the host machine
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
                        else if (handler is Foundation.NSUrlSessionHandler iosHandler)
                        {
                            iosHandler.TrustOverrideForUrl = 
                                (sender, url, trust) => { return true; };
                        }
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

    private void RegisterHubListeners()
    {
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

        // 2. Map connection health events
        _hubConnection.Closed += async (error) =>
        {
            ConnectionStatusChanged?.Invoke("Disconnected", Colors.Red);
            if (error != null)
            {
                LogException("Connection Closed", error);
            }
        };

        _hubConnection.Reconnecting += async (error) =>
        {
            ConnectionStatusChanged?.Invoke("Reconnecting...", Colors.Orange);
            if (error != null)
            {
                LogException("Reconnecting", error);
            }
        };

        _hubConnection.Reconnected += async (connectionId) =>
        {
            ConnectionStatusChanged?.Invoke("Connected", Colors.Green);
        };
    }

    // Explicit Hub Commands with Global Exception Handling
    public async Task StartAsync()
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

    public async Task UpdateLocation(string groupName, double lat, double lng, double heading)
    {
        try
        {
            await _hubConnection.InvokeAsync("UpdateMyLocation", groupName, lat, lng, heading);
        }
        catch (Exception ex)
        {
            LogException(nameof(UpdateLocation), ex);
        }
    }

    // Global Console Logger Helper
    private void LogException(string context, Exception ex)
    {
        // Standard console output
        Console.WriteLine($"[SignalR Exception] {context}: {ex.Message}");

        // Ensure it appears in the MAUI Debug output window
        System.Diagnostics.Debug.WriteLine($"[SignalR Exception] {context}: {ex}");
    }
}