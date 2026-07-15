using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared.Models
{
    // --- NEW: DTO for fetching current group status on reconnect ---
    public class GroupDetailsDto
    {
        public bool IsNavigating { get; set; }
        public string DestName { get; set; }
        public double DestLat { get; set; }
        public double DestLng { get; set; }
    }
}
