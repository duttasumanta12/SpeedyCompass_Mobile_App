using Android.Content;
using Android.OS;
using SpeedyCompass.Services;
using System;

namespace SpeedyCompass.Platforms.Android;

public class AndroidLocationTracker : ILocationTracker
{
    public bool IsSimulating { get; set; } = false;
    public bool IsTracking { get; private set; } = false;

    public static event EventHandler<LocalLocationUpdate> OnLocationUpdatedEvent;

    public event EventHandler<LocalLocationUpdate> LocationUpdated
    {
        add => OnLocationUpdatedEvent += value;
        remove => OnLocationUpdatedEvent -= value;
    }

    public void StartTracking(string groupName, int numberOfOnlineRiders)
    {
        if (IsTracking) return;

        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidLocationService));
        intent.PutExtra("GroupName", groupName);
        intent.PutExtra("NumberOfOnlineRiders", numberOfOnlineRiders);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }

        IsTracking = true;
    }

    public void StopTracking()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidLocationService));
        context.StopService(intent);
        IsTracking = false;
    }

    public static void NotifyLocation(Microsoft.Maui.Devices.Sensors.Location loc, double speedMph, double heading)
    {
        OnLocationUpdatedEvent?.Invoke(null, new LocalLocationUpdate { Location = loc, SpeedMph = speedMph, Heading = heading });
    }

    public void UpdateRiderCount(int count)
    {
        AndroidLocationService.Instance?.UpdateRiderCount(count);
    }
}