using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared.Models
{
    // NEW: DTO for passing profile data to the app
    public class UserProfileDto
    {
        public string Username { get; set; } = string.Empty;
        public string EmergencyContact { get; set; } = string.Empty;
        public string VehicleNumber { get; set; } = string.Empty;
        public string BloodGroup { get; set; } = string.Empty;
        public bool HasConsented { get; set; } = false;
    }
}
