using Android.Content;
using SpeedyCompass.Services;
using System;

namespace SpeedyCompass.Platforms.Android;

public class AndroidLocationTracker : ILocationTracker
{
    public bool IsSimulating { get; set; } = false; // NEW FLAG
    public bool IsTracking { get; private set; } = false;
    // Static event so the Background Service can push data up to the UI
    public static event EventHandler<LocalLocationUpdate> OnLocationUpdatedEvent;

    public event EventHandler<LocalLocationUpdate> LocationUpdated
    {
        add => OnLocationUpdatedEvent += value;
        remove => OnLocationUpdatedEvent -= value;
    }

    public void StartTracking(string groupName, int numberOfOnlineRiders    )
    {
        if(IsTracking) return;

        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidLocationService));
        intent.PutExtra("GroupName", groupName);
        intent.PutExtra("NumberOfOnlineRiders", numberOfOnlineRiders);
        context.StartService(intent);

        IsTracking = true;
    }

    public void StopTracking()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(AndroidLocationService));
        context.StopService(intent);
        IsTracking = false;
    }

    public static void NotifyLocation(Microsoft.Maui.Devices.Sensors.Location loc, double speed, double heading)
    {
        OnLocationUpdatedEvent?.Invoke(null, new LocalLocationUpdate { Location = loc, SpeedMph = speed, Heading = heading });
    }
    public void UpdateRiderCount(int count)
    {
        // Pass the count down to the native Android service
        AndroidLocationService.Instance?.UpdateRiderCount(count);
    }
}