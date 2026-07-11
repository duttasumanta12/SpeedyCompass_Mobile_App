using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Android.Content.PM;
using Android.Locations;
using SpeedyCompass.Services;
using Microsoft.Maui.Controls.Hosting;

namespace SpeedyCompass.Platforms.Android;

[Service(Name = "com.speedycompass.speedycompass.AndroidLocationService", ForegroundServiceType = ForegroundService.TypeLocation)]
public class AndroidLocationService : Service, ILocationListener
{
    private LocationManager _locationManager;
    private SignalRService _signalRService;
    private string _groupName;
    private PowerManager.WakeLock _wakeLock; // NEW: Holds the CPU awake

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

        // --- NEW: ACQUIRE WAKE LOCK TO KEEP SIGNALR & SIMULATION ALIVE ---
        var powerManager = (PowerManager)GetSystemService(PowerService);
        _wakeLock = powerManager.NewWakeLock(WakeLockFlags.Partial, "SpeedyCompass::NavigationWakeLock");
        _wakeLock.Acquire();

        _groupName = intent?.GetStringExtra("GroupName");

        // Grab the active SignalR service from the MAUI Dependency Injection container
        _signalRService = IPlatformApplication.Current?.Services.GetService<SignalRService>();

        var notification = new NotificationCompat.Builder(this, "compass_location_channel")
            .SetContentTitle("SpeedyCompass Active")
            .SetContentText("Routing in progress. Tracking in background.")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .Build();

        StartForeground(10001, notification);

        // --- THE MAGIC: Ask Android OS to push locations to us ---
        _locationManager = (LocationManager)GetSystemService(LocationService);

        if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
        {
            // THE FIX: Increased to 10 seconds (10000ms) and 10 meters to save heavy CPU/Battery load!
            _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, 10000, 10f, this);
        }

        return StartCommandResult.Sticky;
    }

    public async void OnLocationChanged(global::Android.Locations.Location location)
    {
        try
        {
            // THE FIX: Check if we are simulating. If so, ignore the physical hardware GPS ticks!
            // This allows the Foreground Service to KEEP RUNNING to keep the app alive when the screen locks.
            var tracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
            if (tracker != null && tracker.IsSimulating)
            {
                return;
            }

            var mauiLocation = new Microsoft.Maui.Devices.Sensors.Location(location.Latitude, location.Longitude);
            double speedMph = location.HasSpeed ? location.Speed * 2.23694 : 0;
            double heading = location.HasBearing ? location.Bearing : 0;

            // 1. Send to Local UI (If the screen is on and looking at the map)
            AndroidLocationTracker.NotifyLocation(mauiLocation, speedMph, heading);

            // 2. Broadcast to Group (Works perfectly even if screen is locked!)
            if (_signalRService != null && !string.IsNullOrEmpty(_groupName))
            {
                await _signalRService.UpdateLocation(_groupName, Preferences.Default.Get("username", "Unknown"), mauiLocation.Latitude, mauiLocation.Longitude, heading);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Background GPS] Error: {ex.Message}");
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        _locationManager?.RemoveUpdates(this);

        // --- NEW: Release the WakeLock to allow the phone to sleep again ---
        if (_wakeLock != null && _wakeLock.IsHeld)
        {
            _wakeLock.Release();
        }
    }

    // Required Interface methods
    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
    public void OnStatusChanged(string provider, Availability status, Bundle extras) { }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel("compass_location_channel", "Navigation Tracking", NotificationImportance.Low);
            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }
    }
}   