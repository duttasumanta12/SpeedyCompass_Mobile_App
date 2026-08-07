namespace SpeedyCompass.Controls;

public class GroupCreatedEventArgs : EventArgs
{
    public string GroupName { get; set; }
    public int MaxGroupSize { get; set; }
    public int MaxLagDistanceMeters { get; set; }
    public int SplinterWarningDistanceMeters { get; set; }
}

public partial class CreateGroupOverlay : ContentView
{
    public event EventHandler<GroupCreatedEventArgs> GroupCreated;

    public CreateGroupOverlay()
    {
        InitializeComponent();
    }

    public void Show()
    {
        // Reset defaults every time it opens
        NewGroupNameEntry.Text = string.Empty;
        CreateSizeSlider.Value = 10;
        CreateLagSlider.Value = 500;
        CreateSplinterSlider.Value = 2000;
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    // --- Internal Slider Math ---
    private void OnCreateSizeSliderChanged(object sender, ValueChangedEventArgs e) => CreateSizeLabel.Text = $"{(int)Math.Round(e.NewValue)} Riders";

    private void OnCreateLagSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int val = (int)(Math.Round(e.NewValue / 50.0) * 50);
        CreateLagLabel.Text = val == 0 ? "Off" : $"{val}m";
    }

    private void OnCreateSplinterSliderChanged(object sender, ValueChangedEventArgs e)
    {
        int val = (int)(Math.Round(e.NewValue / 100.0) * 100);
        CreateSplinterLabel.Text = val == 0 ? "Off" : $"{val}m";
    }

    // --- Button Actions ---
    private async void OnGenerateClicked(object sender, EventArgs e)
    {
        string groupName = NewGroupNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(groupName))
        {
            // Do internal validation so MainPage doesn't have to worry about it!
            if (Application.Current?.MainPage != null)
                await Application.Current.MainPage.DisplayAlert("Hold Up", "Please enter a name for your convoy.", "OK");
            return;
        }

        Hide();

        var args = new GroupCreatedEventArgs
        {
            GroupName = groupName,
            MaxGroupSize = (int)Math.Round(CreateSizeSlider.Value),
            MaxLagDistanceMeters = (int)(Math.Round(CreateLagSlider.Value / 50.0) * 50),
            SplinterWarningDistanceMeters = (int)(Math.Round(CreateSplinterSlider.Value / 100.0) * 100)
        };

        GroupCreated?.Invoke(this, args);
    }

    private void OnCancelClicked(object sender, EventArgs e) => Hide();
}