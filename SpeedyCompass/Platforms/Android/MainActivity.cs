using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Content.Res;
using System;

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
}