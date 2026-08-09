namespace SpeedyCompass.Controls;

// 1. The Unified Data Packet
public class ConvoySettingsSubmittedEventArgs : EventArgs
{
    public bool IsCreationMode { get; set; }
    public string GroupName { get; set; } // Only populated during Creation
    public int MaxGroupSize { get; set; }
    public int MaxLagDistanceMeters { get; set; }
    public int SplinterWarningDistanceMeters { get; set; }
    public int PitstopDistanceMeters { get; set; }
    public bool EnableDynamicRouting { get; set; }
    public int MinBroadcastDistanceMeters { get; set; }
    public int MaxBroadcastDistanceMeters { get; set; }
}

public partial class ConvoySettingsOverlay : ContentView
{
    public event EventHandler<ConvoySettingsSubmittedEventArgs> SettingsSubmitted;
    private bool _isCreateMode = false;

    public ConvoySettingsOverlay()
    {
        InitializeComponent();
    }

    // =====================================================================
    // --- MODE: CREATE NEW GROUP ---
    // =====================================================================
    public void ShowForCreate()
    {
        _isCreateMode = true;

        HeaderTitleLabel.Text = "Configure Convoy";
        HeaderSubtitleLabel.Text = "Set up the safety parameters for this ride.";

        GroupNameContainer.IsVisible = true;
        GroupNameEntry.Text = string.Empty;

        SubmitButton.Text = "Generate PIN";
        SubmitButton.BackgroundColor = Colors.DarkOrange;

        // Reset to Defaults
        SizeSlider.Value = 10;
        LagSlider.Value = 500;
        SplinterSlider.Value = 2000;
        PitstopSlider.Value = 100;
        DynamicRoutingSwitch.IsToggled = true;
        MinUpdateSlider.Value = 10;
        MaxUpdateSlider.Value = 100;

        IsVisible = true;
    }

    // =====================================================================
    // --- MODE: EDIT EXISTING SETTINGS ---
    // =====================================================================
    public void ShowForEdit(int size, int lag, int splinter, int pitstop, bool dynamicRouting, int minUpdate, int maxUpdate)
    {
        _isCreateMode = false;

        HeaderTitleLabel.Text = "Convoy Settings";
        HeaderSubtitleLabel.Text = "Adjust live telemetry rules.";

        GroupNameContainer.IsVisible = false;

        SubmitButton.Text = "Save Changes";
        SubmitButton.BackgroundColor = Colors.MediumSeaGreen;

        // Inject current values
        SizeSlider.Value = size;
        LagSlider.Value = lag;
        SplinterSlider.Value = splinter;
        PitstopSlider.Value = pitstop;
        DynamicRoutingSwitch.IsToggled = dynamicRouting;
        MinUpdateSlider.Value = minUpdate;
        MaxUpdateSlider.Value = maxUpdate;

        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    // --- Slider Visual Formatting Math ---
    private void OnSizeSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        SizeSlider.Value = roundedValue;
        SizeValueLabel.Text = $"{roundedValue} Riders";
    }

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

    private void OnPitstopSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        PitstopSlider.Value = roundedValue;
        PitstopValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue} km";
    }

    private void OnMinUpdateSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 5.0) * 5;
        MinUpdateSlider.Value = roundedValue;
        MinUpdateValueLabel.Text = $"{roundedValue}m";
    }

    private void OnMaxUpdateSliderChanged(object sender, ValueChangedEventArgs e)
    {
        double roundedValue = Math.Round(e.NewValue / 10.0) * 10;
        MaxUpdateSlider.Value = roundedValue;
        MaxUpdateValueLabel.Text = $"{roundedValue}m";
    }

    // --- Button Actions ---
    private void OnCancelClicked(object sender, EventArgs e) => Hide();

    private async void OnSubmitClicked(object sender, EventArgs e)
    {
        string groupName = GroupNameEntry.Text?.Trim();

        // Prevent generating a PIN if the name is blank
        if (_isCreateMode && string.IsNullOrEmpty(groupName))
        {
            if (Application.Current?.MainPage != null)
                await Application.Current.MainPage.DisplayAlert("Hold Up", "Please enter a name for your convoy.", "OK");
            return;
        }

        Hide();

        var args = new ConvoySettingsSubmittedEventArgs
        {
            IsCreationMode = _isCreateMode,
            GroupName = groupName,
            MaxGroupSize = (int)Math.Round(SizeSlider.Value),
            MaxLagDistanceMeters = (int)Math.Round(LagSlider.Value),
            SplinterWarningDistanceMeters = (int)Math.Round(SplinterSlider.Value),
            PitstopDistanceMeters = (int)Math.Round(PitstopSlider.Value),
            EnableDynamicRouting = DynamicRoutingSwitch.IsToggled,
            MinBroadcastDistanceMeters = (int)Math.Round(MinUpdateSlider.Value),
            MaxBroadcastDistanceMeters = (int)Math.Round(MaxUpdateSlider.Value)
        };

        SettingsSubmitted?.Invoke(this, args);
    }
}