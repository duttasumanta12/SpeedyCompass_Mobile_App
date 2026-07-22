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
    private NotificationManager _notificationManager;

    // Expose instance so we can update the notification live
    public static AndroidLocationService Instance { get; private set; }

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        Instance = this;

        // 1. ALWAYS update the group name and preferences on every call
        _groupName = intent?.GetStringExtra("GroupName");
        Preferences.Default.Set("CurrentGroupName", _groupName);


        _notificationManager = (NotificationManager)GetSystemService(NotificationService);
        CreateNotificationChannel();

        var powerManager = (PowerManager)GetSystemService(PowerService);
        _wakeLock = powerManager.NewWakeLock(WakeLockFlags.Partial, "SpeedyCompass::NavigationWakeLock");
        _wakeLock.Acquire();

        _signalRService = IPlatformApplication.Current?.Services.GetService<SignalRService>();

        int count = intent?.GetIntExtra("NumberOfOnlineRiders", 0) ?? 0;
        var notification = CreateNotification(count);

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
            System.Diagnostics.Debug.WriteLine($"[Background GPS] Security Exception: {ex.Message}");
            StopSelf();
        }


        return StartCommandResult.Sticky;
    }

    // --- NEW: Dynamic Notification Builder ---
    public void UpdateRiderCount(int count)
    {
        if (_notificationManager != null)
        {
            var notification = CreateNotification(count);
            _notificationManager.Notify(10001, notification);
        }
    }

    private Notification CreateNotification(int count)
    {
        // 1. SOS Button Intent (Must be Immutable for Android 12+)
        var sosIntent = new Intent(this, typeof(NotificationActionReceiver));
        sosIntent.SetAction("ACTION_SOS");
        var sosPendingIntent = PendingIntent.GetBroadcast(this, 0, sosIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // 2. Open App Intent
        var mainIntent = new Intent(this, typeof(MainActivity));
        mainIntent.SetAction(Intent.ActionMain);
        mainIntent.AddCategory(Intent.CategoryLauncher);
        mainIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop | ActivityFlags.NewTask);

        var mainPendingIntent = PendingIntent.GetActivity(this, 0, mainIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, "compass_location_channel")
            .SetContentTitle("SpeedyCompass Active")
            .SetContentText($"Routing in progress. {count} Riders in convoy.")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(mainPendingIntent)
            .SetOngoing(true)
            .SetVisibility(NotificationCompat.VisibilityPublic) // Show fully on Lock Screen
            .AddAction(0, "🛑 EMERGENCY SOS", sosPendingIntent) // Add Lock Screen Button
            .Build();
    }

    public async void OnLocationChanged(global::Android.Locations.Location location)
    {
        try
        {
            var tracker = IPlatformApplication.Current?.Services.GetService<ILocationTracker>();
            if (tracker != null && tracker.IsSimulating) return;

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
        Instance = null;
        try { _locationManager?.RemoveUpdates(this); } catch { }
        if (_wakeLock != null && _wakeLock.IsHeld) { _wakeLock.Release(); }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        else
        {
#pragma warning disable CS0618 // Type or member is obsolete
            StopForeground(true);
#pragma warning restore CS0618
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