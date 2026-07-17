using Android.App;
using Android.Content;
using SpeedyCompass.Services;
using Microsoft.Maui.Controls.Hosting;

namespace SpeedyCompass.Platforms.Android;

[BroadcastReceiver(Enabled = true, Exported = false)]
public class NotificationActionReceiver : BroadcastReceiver
{
    public override async void OnReceive(Context context, Intent intent)
    {
        if (intent.Action == "ACTION_SOS")
        {
            // Grab the active SignalR connection
            var signalR = IPlatformApplication.Current?.Services.GetService<SignalRService>();

            // Get credentials from secure preferences
            string groupName = Preferences.Default.Get("CurrentGroupName", "");
            string username = Preferences.Default.Get("username", "Unknown");

            if (signalR != null && !string.IsNullOrEmpty(groupName))
            {
                // Fire the emergency alert directly from the lock screen!
                await signalR.SendGroupAlert(groupName, "Emergency", username);
            }
        }
    }
}