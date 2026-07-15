using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Models
{
    public class TelemetryDto
    {
        public string Name { get; set; }
        public double SpeedKmh { get; set; }
        public double DistanceMeters { get; set; }
        public string Status { get; set; }

        public string SpeedDisplay => $"{Math.Round(SpeedKmh)} km/h";

        public string StatusDisplay
        {
            get
            {
                if (Status == "Lead Rider" || Status == "Awaiting GPS") return Status;
                string dist = DistanceMeters > 1000 ? $"{Math.Round(DistanceMeters / 1000.0, 1)} km" : $"{Math.Round(DistanceMeters)} m";
                return $"{dist} {Status}";
            }
        }

        public Color StatusColor
        {
            get
            {
                if (Status == "Ahead") return Colors.DarkOrange;
                if (Status == "Behind") return Colors.Red;
                if (Status == "Lead Rider") return Colors.MediumPurple;
                return Colors.MediumSeaGreen;
            }
        }
    }
}
