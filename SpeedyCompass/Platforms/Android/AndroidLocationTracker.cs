using Android.Content;
using SpeedyCompass.Services;
using System;

namespace SpeedyCompass.Platforms.Android;

public class AndroidLocationTracker : ILocationTracker
{
    // Static event so the Background Service can push data up to the UI
    public static event EventHandler<LocalLocationUpdate> OnLocationUpdatedEvent;

    public event EventHandler<LocalLocationUpdate> LocationUpdated
    {
        add => OnLocationUpdatedEvent += value;
        remove => OnLocationUpdatedEvent -= value;
    }

    public void StartTracking(string groupName)
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidLocationService));
        intent.PutExtra("GroupName", groupName);

        context.StartForegroundService(intent);
    }

    public void StopTracking()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidLocationService));
        context.StopService(intent);
    }

    public static void NotifyLocation(Microsoft.Maui.Devices.Sensors.Location loc, double speed, double heading)
    {
        OnLocationUpdatedEvent?.Invoke(null, new LocalLocationUpdate { Location = loc, SpeedMph = speed, Heading = heading });
    }
}