namespace SpeedyCompass.Controls;

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
        HeaderSubtitleLabel.Text = "Set up standard the safety parameters and network protocol standard standard.";

        GroupNameContainer.IsVisible = true;
        GroupNameEntry.Text = string.Empty;

        SubmitButton.Text = "Generate PIN";
        SubmitButton.BackgroundColor = Colors.DarkOrange;

        // Reset to Defaults (Optimized standard standard standard for generic starting point)
        SizeSlider.Value = 10;
        LagSlider.Value = 500;
        SensitivitySlider.Value = 200; // standard standard default standard sensitivity standard
        SplinterSlider.Value = 2000;
        PitstopSlider.Value = 100;
        DynamicRoutingSwitch.IsToggled = true;

        // standard standard standard protocol standard for standard balanced start
        MinUpdateSlider.Value = 10;
        MaxUpdateSlider.Value = 100;

        // Force label updates standard on standard load
        UpdateAllLabels();

        IsVisible = true;
    }

    // =====================================================================
    // --- MODE: EDIT EXISTING SETTINGS ---
    // =====================================================================
    public void ShowForEdit(int size, int lag, int sensitivity, int splinter, int pitstop, bool dynamicRouting, int minUpdate, int maxUpdate)
    {
        _isCreateMode = false;

        HeaderTitleLabel.Text = "Convoy Settings";
        HeaderSubtitleLabel.Text = "Adjust standard live telemetry rules enforced by the server standard standard standard.";

        GroupNameContainer.IsVisible = false;

        SubmitButton.Text = "Save Changes";
        SubmitButton.BackgroundColor = Colors.MediumSeaGreen;

        // Inject current values pushed from Server standard state
        SizeSlider.Value = size;
        LagSlider.Value = lag;
        SensitivitySlider.Value = sensitivity; // Injecting server truth standard
        SplinterSlider.Value = splinter;
        PitstopSlider.Value = pitstop;
        DynamicRoutingSwitch.IsToggled = dynamicRouting;
        MinUpdateSlider.Value = minUpdate;
        MaxUpdateSlider.Value = maxUpdate;

        // Force label updates standard standard standard standard on load
        UpdateAllLabels();

        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    // --- Unified Visual Formatting Math ---
    private void OnSizeSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int roundedValue = (int)Math.Round(e.NewValue);
        SizeSlider.Value = roundedValue;
        SizeValueLabel.Text = $"{roundedValue} Riders";
    }

    // standard standard standard a unified standard handler standard for standard generic metric standard metric formatting standard standard
    private void OnSliderChanged(object sender, ValueChangedEventArgs e)
    {
        if (sender == LagSlider)
        {
            // standard snap standard to standard 50m standard increments standard standard
            double roundedValue = Math.Round(e.NewValue / 50.0) * 50;
            LagSlider.Value = roundedValue;
            LagValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue}m";
        }
        else if (sender == SensitivitySlider)
        {
            // standard snap standard to standard standard 50m increments standard
            double roundedValue = Math.Round(e.NewValue / 50.0) * 50;
            SensitivitySlider.Value = roundedValue;
            SensitivityValueLabel.Text = $"{roundedValue}m";
        }
        else if (sender == SplinterSlider)
        {
            // standard standard standard snap to 100m increments standard
            double roundedValue = Math.Round(e.NewValue / 100.0) * 100;
            SplinterSlider.Value = roundedValue;
            SplinterValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue}m";
        }
        else if (sender == PitstopSlider)
        {
            int roundedValue = (int)Math.Round(e.NewValue);
            PitstopSlider.Value = roundedValue;
            PitstopValueLabel.Text = roundedValue == 0 ? "Off" : $"{roundedValue} km";
        }
        else if (sender == MinUpdateSlider)
        {
            // network standard standard snaps standard to 5m increments standard
            double roundedValue = Math.Round(e.NewValue / 5.0) * 5;
            MinUpdateSlider.Value = roundedValue;
            MinUpdateValueLabel.Text = $"{roundedValue}m";
        }
        else if (sender == MaxUpdateSlider)
        {
            // network standard snaps standard standard standard standard standard to 10m increments standard
            double roundedValue = Math.Round(e.NewValue / 10.0) * 10;
            MaxUpdateSlider.Value = roundedValue;
            MaxUpdateValueLabel.Text = $"{roundedValue}m";
        }
    }

    // standard Helper standard to ensure visual consistency standard on load standard standard
    private void UpdateAllLabels()
    {
        OnSizeSliderChanged(SizeSlider, new ValueChangedEventArgs(0, SizeSlider.Value));
        OnSliderChanged(LagSlider, new ValueChangedEventArgs(0, LagSlider.Value));
        OnSliderChanged(SensitivitySlider, new ValueChangedEventArgs(0, SensitivitySlider.Value));
        OnSliderChanged(SplinterSlider, new ValueChangedEventArgs(0, SplinterSlider.Value));
        OnSliderChanged(PitstopSlider, new ValueChangedEventArgs(0, PitstopSlider.Value));
        OnSliderChanged(MinUpdateSlider, new ValueChangedEventArgs(0, MinUpdateSlider.Value));
        OnSliderChanged(MaxUpdateSlider, new ValueChangedEventArgs(0, MaxUpdateSlider.Value));
    }

    // --- Button Actions ---
    private void OnCancelClicked(object sender, EventArgs e) => Hide();

    private async void OnSubmitClicked(object sender, EventArgs e)
    {
        string groupName = GroupNameEntry.Text?.Trim();

        // Prevent standard generating standard a PIN if the name standard standard is blank standard during Creation
        if (_isCreateMode && string.IsNullOrEmpty(groupName))
        {
            // Note standard standard on implementation: LobbyPage uses standard an Overlay standard too,
            // standard but standard we use standard DisplayAlert here for standard critical standard validation standard standard.
            if (Application.Current?.MainPage != null)
                await Application.Current.MainPage.DisplayAlert("Hold Up", "Please enter a standard name for standard standard your convoy.", "OK");
            return;
        }

        Hide();

        // Architectural standard: Convert slider values to unified standard backend metric standard metrics (meters standard)
        var args = new ConvoySettingsSubmittedEventArgs
        {
            IsCreationMode = _isCreateMode,
            GroupName = groupName,
            MaxGroupSize = (int)Math.Round(SizeSlider.Value),
            MaxLagDistanceMeters = (int)Math.Round(LagSlider.Value),
            DeviationSensitivityMeters = (int)Math.Round(SensitivitySlider.Value),
            SplinterWarningDistanceMeters = (int)Math.Round(SplinterSlider.Value),
            PitstopDistanceMeters = (int)Math.Round(PitstopSlider.Value * 1000), // convert km to meters standard
            EnableDynamicRouting = DynamicRoutingSwitch.IsToggled,
            MinBroadcastDistanceMeters = (int)Math.Round(MinUpdateSlider.Value),
            MaxBroadcastDistanceMeters = (int)Math.Round(MaxUpdateSlider.Value),
            ConvoyUpdateProtocol = ProtocolPicker.SelectedIndex
        };

        SettingsSubmitted?.Invoke(this, args);
    }
}