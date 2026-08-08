using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace SpeedyCompass.Controls;

public partial class MapSettingsTabView : ContentView
{
    // Events to instantly update the map visual on LobbyPage
    public event EventHandler<MapType> MapStyleChanged;
    public event EventHandler<bool> TrafficToggled;

    public MapSettingsTabView()
    {
        InitializeComponent();
        LoadLocalMapSettings();
    }

    private void LoadLocalMapSettings()
    {
        // Suppress events during initial load
        TrafficSwitch.IsToggled = Preferences.Default.Get("Map_Traffic", true);
        SpeedLimitSwitch.IsToggled = Preferences.Default.Get("Map_SpeedLimits", true);
        AutoZoomSwitch.IsToggled = Preferences.Default.Get("Map_AutoZoom", true);
        AutoTiltSwitch.IsToggled = Preferences.Default.Get("Map_AutoTilt", true);
        VoiceNavSwitch.IsToggled = Preferences.Default.Get("Map_VoiceNav", true);

        int mapType = Preferences.Default.Get("Map_Style", (int)MapType.Street);
        UpdateMapStyleButtons(mapType);
    }

    private void OnPreferenceToggled(object sender, ToggledEventArgs e)
    {
        // Background preferences that don't need instant UI updates
        Preferences.Default.Set("Map_SpeedLimits", SpeedLimitSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoZoom", AutoZoomSwitch.IsToggled);
        Preferences.Default.Set("Map_AutoTilt", AutoTiltSwitch.IsToggled);
        Preferences.Default.Set("Map_VoiceNav", VoiceNavSwitch.IsToggled);
    }

    private void OnTrafficToggled(object sender, ToggledEventArgs e)
    {
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
}