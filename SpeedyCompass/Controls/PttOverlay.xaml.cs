namespace SpeedyCompass.Controls;

public partial class PttOverlay : ContentView
{
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
        IsVisible = true;
    }

    public void ShowListening(string speakerName)
    {
        PttStatusLabel.Text = "Receiving";
        PttStatusLabel.TextColor = Colors.DodgerBlue;
        PttSpeakerLabel.Text = $"{speakerName} is speaking...";
        PttCountdownLabel.IsVisible = false;
        IsVisible = true;
    }

    public void UpdateCountdown(int secondsLeft)
    {
        PttCountdownLabel.IsVisible = true;
        PttCountdownLabel.Text = $"Closing in {secondsLeft}s...";
    }

    public void Hide()
    {
        IsVisible = false;
    }

    internal void ShowMaximumTimeReached()
    {
        PttCountdownLabel.IsVisible = true;
        PttCountdownLabel.Text = "Maximum speaking time reached.";
    }
}