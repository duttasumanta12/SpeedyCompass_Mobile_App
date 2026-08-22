namespace SpeedyCompass.Controls;

public partial class SafetyActionsTabView : ContentView
{
    public event EventHandler EmergencyStopClicked;
    public event EventHandler RefuelStopClicked;
    public event EventHandler RestStopClicked;
    public event EventHandler LaunchNativeNavClicked;
    public event EventHandler PttClicked;
    private bool _isPttEnabled;

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

    private void OnEmergencyStopClicked(object sender, EventArgs e) => EmergencyStopClicked?.Invoke(this, e);
    private void OnRefuelStopClicked(object sender, EventArgs e) => RefuelStopClicked?.Invoke(this, e);
    private void OnRestStopClicked(object sender, EventArgs e) => RestStopClicked?.Invoke(this, e);
    private void OnLaunchNativeNavClicked(object sender, EventArgs e) => LaunchNativeNavClicked?.Invoke(this, e);
    private void OnPttClicked(object sender, EventArgs e) => PttClicked?.Invoke(this, e);
}