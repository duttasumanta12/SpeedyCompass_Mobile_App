namespace SpeedyCompass.Controls;

public partial class RideSummaryOverlay : ContentView
{
    public event EventHandler CloseRequested;

    public RideSummaryOverlay()
    {
        InitializeComponent();
    }

    // Pass the data directly into the component to format it beautifully!
    public void Show(string destination, string distance, string movingTime, string avgSpeed, string topSpeed, string totalTime)
    {
        SummaryDestLabel.Text = destination;
        SummaryDistLabel.Text = distance;
        SummaryMovingTimeLabel.Text = movingTime;
        SummaryAvgSpeedLabel.Text = avgSpeed;
        SummaryTopSpeedLabel.Text = topSpeed;
        SummaryTotalTimeLabel.Text = totalTime;
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    private void OnCloseClicked(object sender, EventArgs e)
    {
        Hide();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}