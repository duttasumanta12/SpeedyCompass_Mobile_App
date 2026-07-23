using SpeedyCompass.Shared.Constants;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared.Models
{
    // --- NEW: DTO for fetching current group status on reconnect ---
    public class GroupDetailsDto
    {
        public string GroupName { get; set; }
        // --- THE FIX: Sync the Enum to the frontend ---
        public GroupState CurrentState { get; set; } = GroupState.NotNavigating;
        public string DestName { get; set; }
        public double DestLat { get; set; }
        public double DestLng { get; set; }
        public string AdminGoogleId { get; set; }
        public string? JoinCode { get; set; }
    }
}
