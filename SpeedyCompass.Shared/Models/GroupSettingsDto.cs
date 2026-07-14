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
    }
}
