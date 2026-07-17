using SpeedyCompass.Shared.Constants;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared
{
    public static class GroupStateHelper
    {
        public static GroupState GetBreakState(string reason)
        {
            GroupState pauseState = GroupState.PausedBreak;
            if (reason.Contains("Mechanical"))
            {
                pauseState = GroupState.PausedMechanical;
            }
            else if (reason.Contains("Hazard") || reason.Contains("Weather"))
            {
                pauseState = GroupState.PausedHazard;
            }

            return pauseState;
        }
    }
}
