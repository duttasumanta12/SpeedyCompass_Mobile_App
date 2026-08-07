namespace SpeedyCompass.Controls;

// 1. A clean data packet to send back to the Lobby Page
public class SettingsSavedEventArgs : EventArgs
{
    public double MaxLagDistance { get; set; }
    public double SplinterWarning { get; set; }
    public int MaxGroupSize { get; set; }
    public double PitstopReminder { get; set; }
    public bool DynamicRoutingEnabled { get; set; }
    public double MinBroadcastDistance { get; set; }
    public double MaxBroadcastDistance { get; set; }
}

public partial class AdminSettingsOverlay : ContentView
{
    public event EventHandler<SettingsSavedEventArgs> SettingsSaved;
    public event EventHandler SettingsClosed;

    public AdminSettingsOverlay()
    {
        InitializeComponent();
    }

    // Helper to show the overlay and optionally pre-fill existing values
    public void Show()
    {
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }

    // --- Slider UI Logic (Fully encapsulated here!) ---
    private void OnLagSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 50.0) * 50;
        LagSlider.Value = roundedValue;
        LagValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue}m";
    }

    private void OnSplinterSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 100.0) * 100;
        SplinterSlider.Value = roundedValue;
        SplinterValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue}m";
    }
    private void OnSizeSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        SizeSlider.Value = roundedValue;
        SizeValueLabel.Text = $"{roundedValue} Riders";
    }
    private void OnPitstopSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        PitstopSlider.Value = roundedValue;
        PitstopValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue} km";
    }
    private void OnMinUpdateSliderChanged(object sender, ValueChangedEventArgs e)
    {
        MinUpdateValueLabel.Text = $"{Math.Round(e.NewValue)}m";
    }
    private void OnMaxUpdateSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 10.0) * 10;
        MaxUpdateSlider.Value = roundedValue;
        MaxUpdateValueLabel.Text = $"{roundedValue}m";
    }

    // --- Button Actions ---
    private void OnCloseSettingsClicked(object sender, EventArgs e)
    {
        Hide();
        SettingsClosed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSaveSettingsClicked(object sender, EventArgs e)
    {
        Hide();

        // Package the UI data and send it up to the parent page
        var currentSettings = new SettingsSavedEventArgs
        {
            MaxLagDistance = Math.Round(LagSlider.Value),
            SplinterWarning = Math.Round(SplinterSlider.Value),
            MaxGroupSize = (int)Math.Round(SizeSlider.Value),
            PitstopReminder = Math.Round(PitstopSlider.Value),
            DynamicRoutingEnabled = DynamicRoutingSwitch.IsToggled,
            MinBroadcastDistance = Math.Round(MinUpdateSlider.Value),
            MaxBroadcastDistance = Math.Round(MaxUpdateSlider.Value)
        };

        SettingsSaved?.Invoke(this, currentSettings);
    }
}