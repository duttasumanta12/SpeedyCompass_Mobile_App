using SpeedyCompass.Models;

namespace SpeedyCompass.Controls;

public partial class ConvoyRosterTabView : ContentView
{
    // Pass the tapped Rider object back to the LobbyPage!
    public event EventHandler<Rider> RiderTapped;
    public event EventHandler CopyPinClicked;

    public ConvoyRosterTabView()
    {
        InitializeComponent();
    }

    private void OnCopyPinClicked(object sender, EventArgs e) => CopyPinClicked?.Invoke(this, e);

    private void OnRiderTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is Rider selectedRider)
        {
            RiderTapped?.Invoke(this, selectedRider);
        }
    }

    // =====================================================================
    // CLEAN DATA INJECTION METHODS
    // =====================================================================
    public void UpdateConnectionStatus(string status, Color color)
    {
        StatusLabel.Text = status;
        StatusLabel.TextColor = color;
        StatusDot.BackgroundColor = color;

        if (StatusLabel.Parent is View parentView)
        {
            parentView.InvalidateMeasure(); // Forces UI to recalculate width
        }
    }

    public void SetAdminPinCardVisible(bool isVisible)
    {
        AdminPinCard.IsVisible = isVisible;
    }

    public void SetConvoyPin(string pin)
    {
        ConvoyPinLabel.Text = string.IsNullOrEmpty(pin) ? "------" : pin;
    }

    public void SetRidersSource(object riders)
    {
        RidersCollectionView.ItemsSource = null; // Flush cache
        RidersCollectionView.ItemsSource = (System.Collections.IEnumerable)riders;
    }
}