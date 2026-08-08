namespace SpeedyCompass.Controls;

public partial class SensoryAlertOverlay : ContentView
{
    public SensoryAlertOverlay()
    {
        InitializeComponent();
    }

    public async Task TriggerAlertAsync(string alertType, string senderName)
    {
        int durationSeconds = 5;

        // 1. Configure the theme based on the alert type
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
        else
        {
            return; // Unrecognized sensory alert, exit early
        }

        AlertSenderLabel.Text = $"Triggered by: {senderName}";

        Opacity = 0;
        IsVisible = true;

        // 2. Run the flashing & vibration loop!
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

        // 3. Clean up
        Vibration.Default.Cancel();
        IsVisible = false;
        Opacity = 0;
    }
}