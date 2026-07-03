using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Services
{
    public class HardwareButtonService
    {
        public event EventHandler VoxToggleRequested;
        public event EventHandler PttPressed;
        public event EventHandler PttReleased;

        public void TriggerVoxToggle() => VoxToggleRequested?.Invoke(this, EventArgs.Empty);
        public void TriggerPttPress() => PttPressed?.Invoke(this, EventArgs.Empty);
        public void TriggerPttRelease() => PttReleased?.Invoke(this, EventArgs.Empty);
    }
}
