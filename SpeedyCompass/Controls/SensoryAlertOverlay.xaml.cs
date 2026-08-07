namespace SpeedyCompass.Controls;

public partial class SensoryAlertOverlay : ContentView
{
    public SensoryAlertOverlay()
    {
        InitializeComponent();
    }

    public async Task TriggerAlertAsync(string senderName)
    {
        AlertSenderLabel.Text = $"Triggered by: {senderName}";
        Opacity = 0;
        IsVisible = true;

        // Flash animation
        await this.FadeTo(1, 150);
        await Task.Delay(3000); // Hold for 3 seconds
        await this.FadeTo(0, 500);

        IsVisible = false;
    }
}