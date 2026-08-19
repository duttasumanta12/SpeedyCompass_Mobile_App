namespace SpeedyCompass.Controls;

public partial class PttOverlay : ContentView
{
    public event EventHandler? CloseRequested;

    public PttOverlay()
    {
        InitializeComponent();
    }

    public void ShowMicOpen()
    {
        PttStatusLabel.Text = "Mic Open";
        PttStatusLabel.TextColor = Colors.MediumSeaGreen;
        PttSpeakerLabel.Text = "You can now speak to the group.";
        PttCountdownLabel.IsVisible = false;
        ResetSpectrum();
        IsVisible = true;
    }

    public void ShowListening(string speakerName)
    {
        PttStatusLabel.Text = "Receiving";
        PttStatusLabel.TextColor = Colors.DodgerBlue;
        PttSpeakerLabel.Text = $"{speakerName} is speaking...";
        PttCountdownLabel.IsVisible = false;
        ResetSpectrum();
        IsVisible = true;
    }

    public void UpdateCountdown(int secondsLeft)
    {
        PttCountdownLabel.IsVisible = true;
        PttCountdownLabel.Text = $"Closing in {secondsLeft}s...";
    }

    public void UpdateSpectrum(float txLevel, float rxLevel)
    {
        LocalLevelBar.Progress = Math.Clamp(txLevel, 0f, 1f);
        RemoteLevelBar.Progress = Math.Clamp(rxLevel, 0f, 1f);
    }

    public void Hide()
    {
        ResetSpectrum();
        IsVisible = false;
    }

    internal void ShowMaximumTimeReached()
    {
        PttCountdownLabel.IsVisible = true;
        PttCountdownLabel.Text = "Maximum speaking time reached.";
    }

    private void ResetSpectrum()
    {
        LocalLevelBar.Progress = 0;
        RemoteLevelBar.Progress = 0;
    }

    private void OnCloseClicked(object sender, EventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}