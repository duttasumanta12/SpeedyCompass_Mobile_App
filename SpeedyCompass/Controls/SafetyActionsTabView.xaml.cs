namespace SpeedyCompass.Controls;

public partial class SafetyActionsTabView : ContentView
{
    public event EventHandler EmergencyStopClicked;
    public event EventHandler RefuelStopClicked;
    public event EventHandler RestStopClicked;
    public event EventHandler LaunchNativeNavClicked;

    public SafetyActionsTabView() { InitializeComponent(); }

    private void OnEmergencyStopClicked(object sender, EventArgs e) => EmergencyStopClicked?.Invoke(this, e);
    private void OnRefuelStopClicked(object sender, EventArgs e) => RefuelStopClicked?.Invoke(this, e);
    private void OnRestStopClicked(object sender, EventArgs e) => RestStopClicked?.Invoke(this, e);
    private void OnLaunchNativeNavClicked(object sender, EventArgs e) => LaunchNativeNavClicked?.Invoke(this, e);
}