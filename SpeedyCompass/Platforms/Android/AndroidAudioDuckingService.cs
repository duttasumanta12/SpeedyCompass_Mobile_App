using Android.Content;
using Android.Media;
using SpeedyCompass.Services;
using SpeedyCompass.Shared;

[assembly: Microsoft.Maui.Controls.Dependency(typeof(SpeedyCompass.Platforms.Android.AndroidAudioDuckingService))]
namespace SpeedyCompass.Platforms.Android
{
    public class AndroidAudioDuckingService : IAudioDuckingService
    {
        private AudioManager _audioManager;
        private AudioFocusRequestClass _focusRequest;

        public AndroidAudioDuckingService()
        {
            _audioManager = (AudioManager)Microsoft.Maui.ApplicationModel.Platform.AppContext.GetSystemService(global::Android.Content.Context.AudioService);
        }

        public void RequestFocus()
        {
            // "TransientMayDuck" tells Android: "Lower Spotify's volume, but don't pause it."
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
                var focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)
                    .SetAudioAttributes(new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.AssistanceNavigationGuidance)
                        .SetContentType(AudioContentType.Speech)
                        .Build())
                    .Build();
                _audioManager.RequestAudioFocus(focusRequest);
            }
        }

        public void ReleaseFocus()
        {
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O && _focusRequest != null)
            {
                _audioManager.AbandonAudioFocusRequest(_focusRequest);
            }
        }
    }
}