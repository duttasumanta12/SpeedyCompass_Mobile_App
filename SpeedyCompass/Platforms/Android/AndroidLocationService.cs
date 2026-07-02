using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Android.Content.PM;
using Android.Locations;
using SpeedyCompass.Services;
using Microsoft.Maui.Controls.Hosting;

namespace SpeedyCompass.Platforms.Android;

[Service(Name = "com.yourcompany.speedycompass.AndroidLocationService", ForegroundServiceType = ForegroundService.TypeLocation)]
public class AndroidLocationService : Service, ILocationListener
{
    private LocationManager _locationManager;
    private SignalRService _signalRService;
    private string _groupName;

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

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
            // Request updates: Minimum 2000ms delay, AND Minimum 5 meters moved! (Saves massive battery)
            _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, 2000, 5f, this);
        }

        return StartCommandResult.Sticky;
    }

    public async void OnLocationChanged(global::Android.Locations.Location location)
    {
        var mauiLocation = new Microsoft.Maui.Devices.Sensors.Location(location.Latitude, location.Longitude);
        double speedMph = location.HasSpeed ? location.Speed * 2.23694 : 0;
        double heading = location.HasBearing ? location.Bearing : 0;

        // 1. Send to Local UI (If the screen is on and looking at the map)
        AndroidLocationTracker.NotifyLocation(mauiLocation, speedMph, heading);

        // 2. Broadcast to Group (Works perfectly even if screen is locked!)
        if (_signalRService != null && !string.IsNullOrEmpty(_groupName))
        {
            await _signalRService.UpdateLocation(_groupName, mauiLocation.Latitude, mauiLocation.Longitude, heading);
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        _locationManager?.RemoveUpdates(this);
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