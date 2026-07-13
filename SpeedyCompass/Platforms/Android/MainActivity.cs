using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Media;
using Android.OS;
using Android.Views;
using Microsoft.Identity.Client;
using System;
using Stream = Android.Media.Stream;

namespace SpeedyCompass;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    SupportsPictureInPicture = true)]
public class MainActivity : MauiAppCompatActivity
{
    // 1. Event for MAUI to listen to
    public static event Action<bool> OnPiPModeChangedEvent;

    // 2. NEW: Global flag so we ONLY enter PiP when actively navigating
    public static bool IsInNavigationMode { get; set; } = false;
    // --- NEW: Hardware Button Hijacking Variables ---
    private DateTime _volumeDownPressTime;
    private bool _isVolumeDownHeld = false;
    private bool _actionTriggered = false;

    // --- HARDWARE BUTTON HIJACKING (FIXED WITH DISPATCHKEYEVENT) ---

    public override bool DispatchKeyEvent(KeyEvent e)
    {
        // Only intercept if we are actively tracking a route
        if (IsInNavigationMode && e.KeyCode == Keycode.VolumeDown)
        {
            if (e.Action == KeyEventActions.Down)
            {
                if (e.RepeatCount == 0)
                {
                    // 1. Initial Press: Start the 3-second timer
                    _volumeDownPressTime = DateTime.UtcNow;
                    _isVolumeDownHeld = true;
                    _actionTriggered = false;

                    Task.Delay(3000).ContinueWith(_ =>
                    {
                        if (_isVolumeDownHeld && !_actionTriggered)
                        {
                            _actionTriggered = true;
                            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(300));

                            var hwService = IPlatformApplication.Current?.Services.GetService<Services.HardwareButtonService>();
                            hwService?.TriggerPttPress();
                        }
                    });
                }

                // THE MAGIC: Return true IMMEDIATELY on every Down tick. 
                // This completely blocks Android from lowering the volume.
                return true;
            }
            else if (e.Action == KeyEventActions.Up)
            {
                _isVolumeDownHeld = false;

                if (!_actionTriggered)
                {
                    // 2. Short Press (Let go before 3 seconds): 
                    // Manually lower the volume for the user since we blocked it earlier!
                    var audioManager = (AudioManager)GetSystemService(AudioService);
                    audioManager?.AdjustStreamVolume(Stream.Music, Adjust.Lower, VolumeNotificationFlags.ShowUi);
                }

                // Return true to consume the release event
                return true;
            }
        }

        // If it's not Volume Down (or we aren't navigating), let Android handle it normally
        return base.DispatchKeyEvent(e);
    }

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
    }

    // 3. This triggers when the user swipes up to go to the Android Home Screen
    protected override void OnUserLeaveHint()
    {
        base.OnUserLeaveHint();

        // ONLY shrink to PiP if we are actively navigating a route!
        if (IsInNavigationMode)
        {
            EnterPipMode();
        }
    }

    private void EnterPipMode()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var pipBuilder = new PictureInPictureParams.Builder();

            // Set aspect ratio to 1:1 (Square) for a clean dashboard widget look
            var aspectRatio = new Android.Util.Rational(1, 1);
            pipBuilder.SetAspectRatio(aspectRatio);

            EnterPictureInPictureMode(pipBuilder.Build());
        }
    }

    public override void OnPictureInPictureModeChanged(bool isInPictureInPictureMode, Configuration newConfig)
    {
        base.OnPictureInPictureModeChanged(isInPictureInPictureMode, newConfig);
        OnPiPModeChangedEvent?.Invoke(isInPictureInPictureMode);
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(requestCode, resultCode, data);
    }
}