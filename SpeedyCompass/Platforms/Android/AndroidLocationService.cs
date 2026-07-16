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
    private PowerManager.WakeLock _wakeLock;

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

        var powerManager = (PowerManager)GetSystemService(PowerService);
        _wakeLock = powerManager.NewWakeLock(WakeLockFlags.Partial, "SpeedyCompass::NavigationWakeLock");
        _wakeLock.Acquire();

        _groupName = intent?.GetStringExtra("GroupName");
        _signalRService = IPlatformApplication.Current?.Services.GetService<SignalRService>();

        var notification = new NotificationCompat.Builder(this, "compass_location_channel")
            .SetContentTitle("SpeedyCompass Active")
            .SetContentText("Routing in progress. Tracking in background.")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .Build();

        // --- THE FIX: Android 14 (API 34+) Strict Foreground Service Requirements ---
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(10001, notification, ForegroundService.TypeLocation);
        }
        else
        {
            StartForeground(10001, notification);
        }

        _locationManager = (LocationManager)GetSystemService(LocationService);

        try
        {
            if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
            {
                _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, 10000, 10f, this);
            }
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Catch if user revoked location permissions while app was in background
            System.Diagnostics.Debug.WriteLine($"[Background GPS] Security Exception: {ex.Message}");
            StopSelf(); // Gracefully kill the service
        }

        return StartCommandResult.Sticky;
    }

    public async void OnLocationChanged(global::Android.Locations.Location location)
    {
        try
        {
            var tracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
            if (tracker != null && tracker.IsSimulating)
            {
                return;
            }

            var mauiLocation = new Microsoft.Maui.Devices.Sensors.Location(location.Latitude, location.Longitude);
            double speedMph = location.HasSpeed ? location.Speed * 2.23694 : 0;
            double heading = location.HasBearing ? location.Bearing : 0;

            AndroidLocationTracker.NotifyLocation(mauiLocation, speedMph, heading);

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

        try
        {
            _locationManager?.RemoveUpdates(this);
        }
        catch { /* Ignore if it fails to detach */ }

        if (_wakeLock != null && _wakeLock.IsHeld)
        {
            _wakeLock.Release();
        }
    }

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