using System;
using System.Collections.Generic;
using System.Text;

namespace SpeedyCompass.Shared
{
    public interface IAudioDuckingService
    {
        void RequestFocus();
        void ReleaseFocus();
    }
}
