namespace SpeedyCompass.Controls;

public partial class SensoryAlertOverlay : ContentView
{
    public event EventHandler CrashCancelled;
    public event EventHandler CrashEmergencyConfirmed;

    public SensoryAlertOverlay()
    {
        InitializeComponent();
    }

    // NEW: Helper method to generate premium gradients dynamically
    private void ApplyBackgroundGradient(Color startColor, Color endColor)
    {
        AlertBackgroundGrid.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = startColor, Offset = 0.0f },
                new GradientStop { Color = endColor, Offset = 1.0f }
            }
        };
    }

    public async Task TriggerAlertAsync(string alertType, string senderName, string alertMessage = "", Color alertColor = default)
    {
        int durationSeconds = 5;
        CrashControlsContainer.IsVisible = false;
        InputTransparent = true; // Block taps

        if (alertType == "Emergency")
        {
            ApplyBackgroundGradient(Color.FromArgb("#E53935"), Color.FromArgb("#B71C1C")); // Red gradient
            AlertTitleLabel.Text = "EMERGENCY STOP!";
            AlertIconLabel.Text = "warning";
            durationSeconds = 10;
        }
        else if (alertType == "Refuel")
        {
            ApplyBackgroundGradient(Color.FromArgb("#FFB74D"), Color.FromArgb("#F57C00")); // Orange gradient
            AlertTitleLabel.Text = "REFUEL STOP";
            AlertIconLabel.Text = "local_gas_station";
            durationSeconds = 5;
        }
        else if (alertType == "Rest")
        {
            ApplyBackgroundGradient(Color.FromArgb("#64B5F6"), Color.FromArgb("#1976D2")); // Blue gradient
            AlertTitleLabel.Text = "REST STOP";
            AlertIconLabel.Text = "local_cafe";
            durationSeconds = 5;
        }
        else if (alertType == "Weather")
        {
            // Fallback to SolidColorBrush since weather color is passed dynamically
            AlertBackgroundGrid.Background = new SolidColorBrush(alertColor);
            AlertTitleLabel.Text = $"WEATHER ALERT: {alertMessage}";

            // FIX: Changed from senderName to a proper Material Symbol
            AlertIconLabel.Text = "thunderstorm";
            durationSeconds = 5;
        }
        else if (alertType.StartsWith("Formation_"))
        {
            // Parse "Formation_Single_File" -> "Single File"
            string formation = alertType.Replace("Formation_", "").Replace("_", " ");

            // Deep Purple (Highly visible, but distinct from Red/Emergency and Blue/Rest)
            AlertBackgroundGrid.BackgroundColor = Color.Parse("#673AB7");
            AlertTitleLabel.Text = $"FORMATION: {formation.ToUpper()}";

            // 'view_stream' naturally looks like a staggered/single-file road layout
            AlertIconLabel.Text = "view_stream";
            durationSeconds = 6;
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

    public void ShowCrashAlert()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Use gradient instead of flat transparent red for consistency
            ApplyBackgroundGradient(Color.FromArgb("#E53935"), Color.FromArgb("#B71C1C"));

            AlertIconLabel.Text = "car_crash";
            AlertTitleLabel.Text = "CRASH DETECTED";
            AlertSenderLabel.Text = "Are you okay?";

            CrashControlsContainer.IsVisible = true;
            CrashCountdownLabel.Text = "10";

            IsVisible = true;
            Opacity = 1;
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