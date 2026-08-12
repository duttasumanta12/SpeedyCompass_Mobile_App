namespace SpeedyCompass.Controls;

public partial class SensoryAlertOverlay : ContentView
{
    // Expose events so LobbyPage can handle the network calls
    public event EventHandler CrashCancelled;
    public event EventHandler CrashEmergencyConfirmed;

    public SensoryAlertOverlay()
    {
        InitializeComponent();
    }

    // Existing sensory flash logic
    public async Task TriggerAlertAsync(string alertType, string senderName)
    {
        int durationSeconds = 5;
        CrashControlsContainer.IsVisible = false;
        InputTransparent = true; // Block taps

        if (alertType == "Emergency")
        {
            AlertBackgroundGrid.BackgroundColor = Colors.Red;
            AlertTitleLabel.Text = "EMERGENCY STOP!";
            AlertIconLabel.Text = "🛑";
            durationSeconds = 10;
        }
        else if (alertType == "Refuel")
        {
            AlertBackgroundGrid.BackgroundColor = Colors.DarkOrange;
            AlertTitleLabel.Text = "REFUEL STOP";
            AlertIconLabel.Text = "⛽";
            durationSeconds = 5;
        }
        else if (alertType == "Rest")
        {
            AlertBackgroundGrid.BackgroundColor = Colors.DodgerBlue;
            AlertTitleLabel.Text = "REST STOP";
            AlertIconLabel.Text = "☕";
            durationSeconds = 5;
        }
        else { return; }

        AlertSenderLabel.Text = $"Triggered by: {senderName}";
        Opacity = 0;
        IsVisible = true;

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
                await this.FadeTo(0.8, 250);
                await this.FadeTo(0.2, 250);
            }
        }
        catch (TaskCanceledException) { }

        HideAlert();
    }

    // =====================================================================
    // NEW: DEDICATED CRASH UI LOGIC
    // =====================================================================
    public void ShowCrashAlert()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AlertBackgroundGrid.BackgroundColor = Color.FromArgb("#E6D32F2F"); // Transparent Red
            AlertIconLabel.Text = "⚠️";
            AlertTitleLabel.Text = "CRASH DETECTED";
            AlertSenderLabel.Text = "Are you okay?";

            CrashControlsContainer.IsVisible = true;
            CrashCountdownLabel.Text = "10";

            IsVisible = true;
            Opacity = 1;

            // THE FIX: Must be false so the buttons can actually be clicked!
            InputTransparent = false;
        });
    }

    public void UpdateCrashCountdown(int seconds)
    {
        MainThread.BeginInvokeOnMainThread(() => CrashCountdownLabel.Text = seconds.ToString());
    }

    public void HideAlert()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsVisible = false;
            Opacity = 0;
            InputTransparent = true;
            CrashControlsContainer.IsVisible = false;
            Vibration.Default.Cancel();
        });
    }

    private void OnCrashCancelledClicked(object sender, EventArgs e) => CrashCancelled?.Invoke(this, EventArgs.Empty);
    private void OnCrashEmergencyClicked(object sender, EventArgs e) => CrashEmergencyConfirmed?.Invoke(this, EventArgs.Empty);
}