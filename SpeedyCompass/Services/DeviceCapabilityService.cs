namespace SpeedyCompass.Services;

public class DeviceCapabilityService
{
    public async Task<bool> RequestRequiredPermissionsAsync()
    {
        // ... (Your existing location/mic/notification code stays exactly the same) ...
        var locStatus = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (locStatus != PermissionStatus.Granted)
        {
            locStatus = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (locStatus != PermissionStatus.Granted) return false;
        }

        var micStatus = await Permissions.CheckStatusAsync<Permissions.Microphone>();
        if (micStatus != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.Microphone>();

        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Version.Major >= 13)
        {
            var notifStatus = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (notifStatus != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }

        return true;
    }

    public async Task RequestBackgroundExecutionOverridesAsync(Page parentPage)
    {
#if ANDROID
        var context = global::Android.App.Application.Context;
        var pm = (global::Android.OS.PowerManager)context.GetSystemService(global::Android.Content.Context.PowerService);
        var packageName = context.PackageName;

        if (!pm.IsIgnoringBatteryOptimizations(packageName))
        {
            bool accept = await parentPage.DisplayAlert(
                "Background Tracking",
                "To keep navigation active while your screen is locked, please allow unrestricted background activity on the next screen.",
                "OK", "Cancel");

            if (accept)
            {
                try
                {
                    // Attempt 1: Direct dialog (Standard Android)
                    var intent = new global::Android.Content.Intent();
                    intent.SetAction(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                    intent.SetData(global::Android.Net.Uri.Parse("package:" + packageName));
                    intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                    context.StartActivity(intent);
                }
                catch (Exception)
                {
                    // Attempt 2: Fallback for Xiaomi/Huawei/Samsung restrictive skins
                    try
                    {
                        var fallbackIntent = new global::Android.Content.Intent();
                        fallbackIntent.SetAction(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);
                        fallbackIntent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                        context.StartActivity(fallbackIntent);
                    }
                    catch
                    {
                        // If everything fails, silently swallow so the app doesn't crash.
                        // They will just drop offline when their screen turns off.
                    }
                }
            }
        }
#endif
        await Task.CompletedTask;
    }
}