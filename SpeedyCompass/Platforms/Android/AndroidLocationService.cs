using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Android.Content.PM;

namespace SpeedyCompass.Platforms.Android;

// Giving it an explicit Name makes it easier to register in the AndroidManifest.xml
[Service(Name = "com.speedycompass.speedycompass.AndroidLocationService", ForegroundServiceType = ForegroundService.TypeLocation)]
public class AndroidLocationService : Service
{
    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        CreateNotificationChannel();

        // Android REQUIRES a visible notification to run tasks in the background indefinitely
        var notification = new NotificationCompat.Builder(this, "compass_location_channel")
            .SetContentTitle("Speedy Compass Active")
            .SetContentText("Broadcasting your location to the group...")
            .SetSmallIcon(Resource.Mipmap.appicon) // Ensure this icon exists in your Resources
            .SetOngoing(true)
            .Build();

        // Start as a foreground service to prevent Android from killing the app
        StartForeground(10001, notification);

        // Returning Sticky tells the OS to recreate the service if it ever gets killed for memory
        return StartCommandResult.Sticky;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel("compass_location_channel", "Location Tracking", NotificationImportance.Low);
            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }
    }
}