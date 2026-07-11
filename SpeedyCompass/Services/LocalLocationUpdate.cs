using System;
using Microsoft.Maui.Devices.Sensors;

namespace SpeedyCompass.Services;

public class LocalLocationUpdate
{
    public Location Location { get; set; }
    public double SpeedMph { get; set; }
    public double Heading { get; set; }
}

public interface ILocationTracker
{
    // --- NEW FLAG FOR SIMULATION & WAKE LOCKS ---
    bool IsSimulating { get; set; }
    void StartTracking(string groupName);
    void StopTracking();

    // Event to update the local map UI when the screen is actually on
    event EventHandler<LocalLocationUpdate> LocationUpdated;
}