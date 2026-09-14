namespace SpeedyCompass.Controls;

public partial class SafetyActionsTabView : ContentView
{
    public event EventHandler EmergencyStopClicked;
    public event EventHandler RefuelStopClicked;
    public event EventHandler RestStopClicked;
    public event EventHandler LaunchNativeNavClicked;
    public event EventHandler PttClicked;
    public event EventHandler FormationChangeClicked;
    private bool _isPttEnabled;
    private bool _isActionLocked = false;

    public SafetyActionsTabView() { InitializeComponent(); }

    public void SetPttVisible(bool isVisible)
    {
        PttCard.IsVisible = isVisible;
    }
    public void SetPttEnabled(bool isEnabled)
    {
        _isPttEnabled = isEnabled;
        PttCard.Opacity = isEnabled ? 1.0 : 0.45;
    }

    // =====================================================================
    // UNIFIED ACTION HANDLER
    // =====================================================================
    private async void ExecuteLockedAction(Action actionToFire)
    {
        // 1. If an action is already processing, instantly swallow the click
        if (_isActionLocked) return;

        // 2. Lock the panel
        _isActionLocked = true;

        // 3. Fire the event up to LobbyPage (which will handle the visual fading)
        actionToFire?.Invoke();

        // 4. Safety fallback: Auto-unlock after 3 seconds in case of a network error.
        // (Normally, LobbyPage will re-enable the UI on its own when finished).
        await Task.Delay(3000);
        _isActionLocked = false;
    }

    // Wrap your alert buttons in the lock
    private void OnEmergencyStopClicked(object sender, EventArgs e)
        => ExecuteLockedAction(() => EmergencyStopClicked?.Invoke(this, e));

    private void OnRefuelStopClicked(object sender, EventArgs e)
        => ExecuteLockedAction(() => RefuelStopClicked?.Invoke(this, e));

    private void OnRestStopClicked(object sender, EventArgs e)
        => ExecuteLockedAction(() => RestStopClicked?.Invoke(this, e));
    private void OnLaunchNativeNavClicked(object sender, EventArgs e) => LaunchNativeNavClicked?.Invoke(this, e);
    private void OnPttClicked(object sender, EventArgs e) => PttClicked?.Invoke(this, e);
    private void OnFormationChangeClicked(object sender, EventArgs e)
        => ExecuteLockedAction(() => FormationChangeClicked?.Invoke(this, e));

}