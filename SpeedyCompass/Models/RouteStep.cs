using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Models
{
    // --- INTERNAL APP MODEL ---
    public class RouteStep
    {
        public Location TurnLocation { get; set; }
        public string Instruction { get; set; }
        public bool VoiceAlertPlayed { get; set; } = false;
    }
}
