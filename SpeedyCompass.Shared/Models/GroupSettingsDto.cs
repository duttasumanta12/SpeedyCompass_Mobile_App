using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared.Models
{
    // 1. NEW: Add this DTO class at the top of your file
    public class GroupSettingsDto
    {
        public int MaxLagDistanceMeters { get; set; }
        public int SplinterWarningDistanceMeters { get; set; }
        public int MaxGroupSize { get; set; }
        public int PitstopDistanceMeters { get; set; }
        public bool EnableDynamicRouting { get; set; }
        public int ArrivalGeofenceMeters { get; set; }
        public string? LeadRiderGoogleId { get; set; }
        // --- NEW: Dynamic Telemetry Throttle Range ---
        public int MinUpdateDistanceMeters { get; set; } = 10;
        public int MaxUpdateDistanceMeters { get; set; } = 100;
    }
}
