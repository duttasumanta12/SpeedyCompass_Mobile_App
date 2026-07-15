using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared.Models
{
    public class TelemetryDto
    {
        public string Name { get; set; }
        public double SpeedKmh { get; set; }
        public double DistanceMeters { get; set; }
        public string Status { get; set; } // "Ahead", "Behind", "Lead", or "Awaiting GPS"
    }
}
