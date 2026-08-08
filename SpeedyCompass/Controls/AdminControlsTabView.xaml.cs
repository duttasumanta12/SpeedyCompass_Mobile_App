using SpeedyCompass.Shared.Constants;
using SpeedyCompass.Shared.Models; // Ensure GroupState is accessible!

namespace SpeedyCompass.Controls;

public partial class AdminControlsTabView : ContentView
{
    public event EventHandler PauseNavClicked;
    public event EventHandler ResumeNavClicked;
    public event EventHandler MeetupPointClicked;
    public event EventHandler AdminSettingsClicked;
    public event EventHandler CompleteNavClicked;
    public event EventHandler ResetTripClicked;

    public AdminControlsTabView() { InitializeComponent(); }

    private void OnPauseNavClicked(object sender, EventArgs e) => PauseNavClicked?.Invoke(this, e);
    private void OnResumeJourneyClicked(object sender, EventArgs e) => ResumeNavClicked?.Invoke(this, e);
    private void OnGenerateMeetupClicked(object sender, EventArgs e) => MeetupPointClicked?.Invoke(this, e);
    private void OnAdminSettingsClicked(object sender, EventArgs e) => AdminSettingsClicked?.Invoke(this, e);
    private void OnCompleteNavClicked(object sender, EventArgs e) => CompleteNavClicked?.Invoke(this, e);
    private void OnResetDestinationClicked(object sender, EventArgs e) => ResetTripClicked?.Invoke(this, e);

    // THE FIX: We moved all the UI toggling logic in here!
    public void UpdateVisibility(GroupState currentState, bool amIAdmin, bool hasMultipleRiders)
    {
        if (!amIAdmin)
        {
            PauseNavBtn.IsVisible = false;
            ResumeNavBtn.IsVisible = false;
            CompleteNavBtn.IsVisible = false;
            MeetupPointBtn.IsVisible = false;
            ResetTripBtn.IsVisible = false;
            return;
        }

        PauseNavBtn.IsVisible = (currentState == GroupState.Navigating);

        ResumeNavBtn.IsVisible = (currentState == GroupState.PausedBreak ||
                                  currentState == GroupState.PausedHazard ||
                                  currentState == GroupState.PausedMechanical);

        CompleteNavBtn.IsVisible = (currentState == GroupState.Navigating || ResumeNavBtn.IsVisible);

        ResetTripBtn.IsVisible = (CompleteNavBtn.IsVisible || currentState == GroupState.DestinationSet);

        MeetupPointBtn.IsVisible = hasMultipleRiders && (currentState == GroupState.Navigating || currentState == GroupState.DestinationSet || ResumeNavBtn.IsVisible);
    }
}