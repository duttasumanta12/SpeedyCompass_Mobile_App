namespace SpeedyCompass.Controls;

public partial class LiveTelemetryHeaderView : ContentView
{
    // Re-exposing the drawer gesture events so LobbyPage can handle the physics
    public event EventHandler<TappedEventArgs> HeaderTapped;
    public event EventHandler<PanUpdatedEventArgs> HeaderPanUpdated;

    public LiveTelemetryHeaderView()
    {
        InitializeComponent();
    }

    private void OnHeaderTapped(object sender, TappedEventArgs e) => HeaderTapped?.Invoke(this, e);
    private void OnHeaderPanUpdated(object sender, PanUpdatedEventArgs e) => HeaderPanUpdated?.Invoke(this, e);

    // =====================================================================
    // CLEAN DATA INJECTION METHODS
    // =====================================================================
    public void SetOriginCoordinates(double lat, double lng)
    {
        TelemetryOriginLabel.Text = $"{lat:F5}, {lng:F5}";
    }

    public void SetDestinationName(string destName)
    {
        TelemetryDestLabel.Text = destName;
    }

    public void UpdateTelemetryStats(string distText, Color distColor, string totalTravel, string totalRoute, double progressVal, string progressPercent, string eta, bool isOffRoute)
    {
        MyDistanceLabel.Text = distText;
        MyDistanceLabel.TextColor = distColor;

        MyEtaLabel.IsVisible = !isOffRoute;
        if (!isOffRoute) MyEtaLabel.Text = eta;

        MyTotalTraveledLabel.Text = totalTravel;
        MyTotalRouteLabel.Text = totalRoute;

        MyProgressPercentLabel.Text = progressPercent;
        RouteProgressBar.ProgressTo(progressVal, 500, Easing.Linear);
    }
    public void UpdateSpeed(string speedStr)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (MySpeedLabel != null) MySpeedLabel.Text = speedStr;
        });
    }
}