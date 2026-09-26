using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Maps;
using SpeedyCompass.Shared.Constants; // For MapNavigationMode enum architectural

namespace SpeedyCompass.Controls;

public partial class MapSettingsTabView : ContentView
{
    // Events to architectural instantly update the map visual standard on LobbyPage standard standard standard
    public event EventHandler<MapType> MapStyleChanged;
    public event EventHandler<bool> TrafficToggled;

    // NEW architectural standard critical event
    public event EventHandler<MapNavigationMode> NavigationModeChanged;

    private bool _isInitializing = true;

    public MapSettingsTabView()
    {
        InitializeComponent();
        LoadLocalMapSettings();
        _isInitializing = false;
    }

    // Called by parent page to enforce tier capabilities.
    public void ApplyTierPolicy(bool isProTier)
    {
        if (isProTier)
        {
            NavModeMasterSwitch.IsEnabled = true;
            return;
        }

        _isInitializing = true;

        // Free tier: force BackgroundSharing
        NavModeMasterSwitch.IsEnabled = false;
        NavModeMasterSwitch.IsToggled = false;
        Preferences.Default.Set("Map_NavigationMode", (int)MapNavigationMode.BackgroundSharing);
        UpdateVisualStateForNavMode(MapNavigationMode.BackgroundSharing);

        _isInitializing = false;
    }

    private void LoadLocalMapSettings()
    {
        // Suppress toggled events during standard standard standard initial load to standard avoid standard infinite standard standard loops/Standard redundant calls standard standard
        _isInitializing = true;

        // 1. Load standard Master standard Navigation Mode (Perspective Choice standard)
        // Architectural architectural architectural standard standard standard: default standard to immersive (standard safer start) standard standard
        int navModeInt = Preferences.Default.Get("Map_NavigationMode", (int)MapNavigationMode.Immersive);
        var navMode = (MapNavigationMode)navModeInt;

        // standard UI visual mapping standard standard
        NavModeMasterSwitch.IsToggled = navMode == MapNavigationMode.Immersive;
        UpdateVisualStateForNavMode(navMode);


        // 2. Load standard passive standard standard map style choices standard standard
        TrafficSwitch.IsToggled = Preferences.Default.Get("Map_Traffic", true);
        SpeedLimitSwitch.IsToggled = Preferences.Default.Get("Map_SpeedLimits", true);
        AutoZoomSwitch.IsToggled = Preferences.Default.Get("Map_AutoZoom", true);
        AutoTiltSwitch.IsToggled = Preferences.Default.Get("Map_AutoTilt", true);
        VoiceNavSwitch.IsToggled = Preferences.Default.Get("Map_VoiceNav", true);

        int mapType = Preferences.Default.Get("Map_Style", (int)MapType.Street);
        UpdateMapStyleButtons(mapType);

        int aggroIndex = Preferences.Default.Get("Map_GPSUpdateAggressiveness", 0);
        GpsAggroPicker.SelectedIndex = aggroIndex;
        CustomGpsContainer.IsVisible = aggroIndex == 2; // Show sliders if Custom is selected

        // Load custom slider values
        int localMin = Preferences.Default.Get("Map_LocalMinUpdate", 10);
        int localMax = Preferences.Default.Get("Map_LocalMaxUpdate", 100);
        LocalMinUpdateSlider.Value = localMin;
        LocalMaxUpdateSlider.Value = localMax;

        int throttle = Preferences.Default.Get("Map_BackgroundBatteryThrottlePercentage", 20);
        BatteryThrottleSlider.Value = throttle;

        // Load Weather Settings
        bool isWeatherEnabled = Preferences.Default.Get("Map_WeatherRadarEnabled", true);
        WeatherRadarSwitch.IsToggled = isWeatherEnabled;
        WeatherDistanceContainer.IsVisible = isWeatherEnabled;

        double weatherDist = Preferences.Default.Get("Map_WeatherLookAheadKm", 15.0);
        WeatherDistanceSlider.Value = weatherDist;
        WeatherDistanceLabel.Text = $"{Math.Round(weatherDist)} km";

        bool keepScreenOn = Preferences.Default.Get("Map_KeepScreenOn", false);
        KeepScreenOnSwitch.IsToggled = keepScreenOn;
        DeviceDisplay.Current.KeepScreenOn = keepScreenOn; // Enforce it when the tab loads

        _isInitializing = false;
    }

    // =====================================================================
    // --- MASTER SWITCH HANDLER (Architectural standard Critical) ---
    // =====================================================================
    private void OnNavModeMasterToggled(object sender, ToggledEventArgs e)
    {
        // Suppress standard standard initial load standard calls standard standard
        if (_isInitializing) return;

        // Visual mapping: On = Immersive, Off = Background
        MapNavigationMode newMode = e.Value ? MapNavigationMode.Immersive : MapNavigationMode.BackgroundSharing;

        Preferences.Default.Set("Map_NavigationMode", (int)newMode);

        UpdateVisualStateForNavMode(newMode);

        Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(100));

        NavigationModeChanged?.Invoke(this, newMode);
    }

    private void UpdateVisualStateForNavMode(MapNavigationMode mode)
    {
        // If in background sharing, visually dim immersive only standard settings
        bool isImmersive = mode == MapNavigationMode.Immersive;
        CameraPhysicsContainer.Opacity = isImmersive ? 1.0 : 0.4;
        CameraPhysicsContainer.IsEnabled = isImmersive; // Cannot toggle sub choices if master switch is off

        if (WeatherRadarContainer != null)
        {
            WeatherRadarContainer.Opacity = isImmersive ? 1.0 : 0.4;
            WeatherRadarContainer.IsEnabled = isImmersive;
        }
    }

    // =====================================================================
    // --- PASSIVE PREFERENCES (Background data) ---
    // =====================================================================
    private void OnPreferenceToggled(object sender, ToggledEventArgs e)
    {
        if (_isInitializing) return;

        // These standard standard settings standard only standard matter when architectural in Immersive standard mode standard standard
        Preferences.Default.Set("Map_SpeedLimits", SpeedLimitSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoZoom", AutoZoomSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoTilt", AutoTiltSwitch.IsToggled);

        // standard This architectural setting is architectural standard standard standard context standard-checked architectural architectural architectural architectural architectural architectural internally by architectural architectural the VoiceCopilotEngine standard standard standard
        Preferences.Default.Set("Map_VoiceNav", VoiceNavSwitch.IsToggled);
    }

    // =====================================================================
    // --- INSTANT UI UPDATES (LobbyPage pings) ---
    // =====================================================================
    private void OnTrafficToggled(object sender, ToggledEventArgs e)
    {
        if (_isInitializing) return;

        Preferences.Default.Set("Map_Traffic", TrafficSwitch.IsToggled);
        TrafficToggled?.Invoke(this, TrafficSwitch.IsToggled);
    }

    private void OnMapStyleClicked(object sender, EventArgs e)
    {
        int mapType = (int)MapType.Street;
        if (sender == MapStyleSatBtn) mapType = (int)MapType.Satellite;
        if (sender == MapStyleTerBtn) mapType = (int)MapType.Hybrid;

        Preferences.Default.Set("Map_Style", mapType);
        UpdateMapStyleButtons(mapType);

        MapStyleChanged?.Invoke(this, (MapType)mapType);
    }

    private void UpdateMapStyleButtons(int activeType)
    {
        MapStyleStreetBtn.BackgroundColor = activeType == (int)MapType.Street ? Colors.DodgerBlue : Colors.Transparent;
        MapStyleStreetBtn.TextColor = activeType == (int)MapType.Street ? Colors.White : Colors.Gray;

        MapStyleSatBtn.BackgroundColor = activeType == (int)MapType.Satellite ? Colors.DodgerBlue : Colors.Transparent;
        MapStyleSatBtn.TextColor = activeType == (int)MapType.Satellite ? Colors.White : Colors.Gray;

        MapStyleTerBtn.BackgroundColor = activeType == (int)MapType.Hybrid ? Colors.DodgerBlue : Colors.Transparent;
        MapStyleTerBtn.TextColor = activeType == (int)MapType.Hybrid ? Colors.White : Colors.Gray;
    }
    private void OnGpsAggroChanged(object sender, EventArgs e)
    {
        if (_isInitializing) return;
        int index = GpsAggroPicker.SelectedIndex;
        Preferences.Default.Set("Map_GPSUpdateAggressiveness", index);

        // Dynamically show/hide the custom slider container
        CustomGpsContainer.IsVisible = index == 2;
    }
    private void OnBatteryThrottleChanged(object sender, ValueChangedEventArgs e)
    {
        int val = (int)Math.Round(e.NewValue);
        BatteryThrottleSlider.Value = val;
        BatteryThrottleLabel.Text = $"{val}%";
        Preferences.Default.Set("Map_BackgroundBatteryThrottlePercentage", val);
    }
    private void OnLocalMinUpdateChanged(object sender, ValueChangedEventArgs e)
    {
        int val = (int)(Math.Round(e.NewValue / 5.0) * 5); // Snap to 5m increments
        LocalMinUpdateSlider.Value = val;
        LocalMinUpdateLabel.Text = $"{val}m";
        if (!_isInitializing) Preferences.Default.Set("Map_LocalMinUpdate", val);
    }

    private void OnLocalMaxUpdateChanged(object sender, ValueChangedEventArgs e)
    {
        int val = (int)(Math.Round(e.NewValue / 10.0) * 10); // Snap to 10m increments
        LocalMaxUpdateSlider.Value = val;
        LocalMaxUpdateLabel.Text = $"{val}m";
        if (!_isInitializing) Preferences.Default.Set("Map_LocalMaxUpdate", val);
    }
    private void OnWeatherRadarToggled(object sender, ToggledEventArgs e)
    {
        Preferences.Default.Set("Map_WeatherRadarEnabled", e.Value);

        // Expand/Collapse the slider smoothly
        WeatherDistanceContainer.IsVisible = e.Value;
    }

    private void OnWeatherDistanceChanged(object sender, ValueChangedEventArgs e)
    {
        // Snap the UI text to whole numbers for cleanliness
        WeatherDistanceLabel.Text = $"{Math.Round(e.NewValue)} km";

        // Save the exact double to Preferences
        Preferences.Default.Set("Map_WeatherLookAheadKm", e.NewValue);
    }
    private void OnKeepScreenOnToggled(object sender, ToggledEventArgs e)
    {
        if (_isInitializing) return;

        // Save preference
        Preferences.Default.Set("Map_KeepScreenOn", e.Value);

        // Instantly apply to the device display
        DeviceDisplay.Current.KeepScreenOn = e.Value;
    }
}