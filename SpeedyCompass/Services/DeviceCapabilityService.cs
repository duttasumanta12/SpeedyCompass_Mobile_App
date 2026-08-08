namespace SpeedyCompass.Services;

public class DeviceCapabilityService
{
    public async Task<bool> RequestRequiredPermissionsAsync()
    {
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
        var pm = (global::Android.OS.PowerManager)global::Android.App.Application.Context.GetSystemService(global::Android.Content.Context.PowerService);
        if (!pm.IsIgnoringBatteryOptimizations(global::Android.App.Application.Context.PackageName))
        {
            bool accept = await parentPage.DisplayAlert("Background Tracking", "To keep navigation active while your screen is locked, please allow unrestricted background activity on the next screen.", "OK", "Cancel");
            if (accept)
            {
                var intent = new global::Android.Content.Intent();
                intent.SetAction(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(global::Android.Net.Uri.Parse("package:" + global::Android.App.Application.Context.PackageName));
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
        }
#endif
        await Task.CompletedTask;
    }
}