namespace SpeedyCompass.Controls;

public partial class LocationDisabledOverlay : ContentView
{
    public event EventHandler OpenSettingsRequested;
    public event EventHandler RetryRequested;

    public LocationDisabledOverlay()
    {
        InitializeComponent();
    }

    public void Show() => IsVisible = true;
    public void Hide() => IsVisible = false;

    private void OnOpenSettingsClicked(object sender, EventArgs e) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnRetryClicked(object sender, EventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);
}