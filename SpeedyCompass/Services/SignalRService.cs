using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace SpeedyCompass.Services;

public class SignalRService
{
    private readonly HubConnection _hubConnection;

    // Standard C# events that UI pages can subscribe to
    public event Action<List<Rider>> RosterUpdated;
    public event Action<double, double, string> NavigationStarted;
    public event Action<string, Color> ConnectionStatusChanged;
    public event Action<string, double, double, double> RiderLocationUpdated;

    public SignalRService()
    {
        // Handle cross-platform localhost URLs for the emulators
        // Note: Check your backend's launchSettings.json to ensure the port is correct (e.g., 5000)
        string baseUrl = DeviceInfo.Platform == DevicePlatform.Android
            ? "https://speedycompassbe-dme4f2hncnb0e4ad.southcentralus-01.azurewebsites.net/"  // Android emulator maps 10.0.2.2 to the host machine
            : "https://localhost:7219"; // iOS Simulator and Windows/Mac use standard localhost

        // IMPORTANT: If testing on PHYSICAL devices on your local Wi-Fi, 
        // you must hardcode your host machine's local IP address instead:
        // baseUrl = "http://192.168.1.X:5000"; 

        _hubConnection = new HubConnectionBuilder()
               .WithUrl($"{baseUrl}/compasshub", options =>
               {
                   // Long Polling
                   options.Transports = HttpTransportType.LongPolling;

                   // Configure custom HttpClientHandler to bypass SSL certificate validation for local development
                   options.HttpMessageHandlerFactory = handler =>
                   {
                       if (handler is HttpClientHandler clientHandler)
                       {
                           clientHandler.ServerCertificateCustomValidationCallback =
                               (message, cert, chain, errors) => { return true; };
                       }
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

        _hubConnection.On<string, double, double, double>("ReceiveRiderLocation", (riderId, lat, lng, heading) =>
        {
            RiderLocationUpdated?.Invoke(riderId, lat, lng, heading);
        });

        // 2. Map connection health events
        _hubConnection.Closed += async (error) =>
        {
            ConnectionStatusChanged?.Invoke("Disconnected", Colors.Red);
        };

        _hubConnection.Reconnecting += async (error) =>
        {
            ConnectionStatusChanged?.Invoke("Reconnecting...", Colors.Orange);
        };

        _hubConnection.Reconnected += async (connectionId) =>
        {
            ConnectionStatusChanged?.Invoke("Connected", Colors.Green);
        };

    }

    // Explicit Hub Commands
    public async Task StartAsync()
    {
        try
        {
            await _hubConnection.StartAsync();
        }
        catch (Exception ex)
        {
            throw ex; // Handle connection errors appropriately in your app
        }
    }

    public async Task<bool> CheckGroupExists(string groupName)
    {
        try
        {
            return await _hubConnection.InvokeAsync<bool>("CheckGroupExists", groupName);
        }
        catch(Exception ex)
        {
            throw ex;
        }
    }

    public async Task CreateGroup(string groupName, string userName) =>
        await _hubConnection.InvokeAsync("CreateGroup", groupName, userName);

    public async Task JoinGroup(string groupName, string userName) =>
        await _hubConnection.InvokeAsync("JoinGroup", groupName, userName);

    public async Task StartGroupNavigation(string groupName, double lat, double lng, string destName) =>
        await _hubConnection.InvokeAsync("StartNavigation", groupName, lat, lng, destName);

    public async Task UpdateLocation(string groupName, double lat, double lng, double heading) =>
        await _hubConnection.InvokeAsync("UpdateMyLocation", groupName, lat, lng, heading);
}