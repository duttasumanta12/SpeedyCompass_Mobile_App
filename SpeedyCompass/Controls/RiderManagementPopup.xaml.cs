namespace SpeedyCompass.Controls;

public partial class RiderManagementOverlay : ContentView
{
    // The event that tells the LobbyPage what button was clicked
    public event EventHandler<string> ActionSelected;

    private bool _isHidden;

    public RiderManagementOverlay()
    {
        InitializeComponent();
    }

    public void Show(string riderName, bool isHidden, bool amIAdmin, bool isSelf)
    {
        _isHidden = isHidden;
        TitleLabel.Text = $"Manage {riderName}";

        // Admin checks
        AdminOptionsLayout.IsVisible = amIAdmin;

        // Visibility toggles
        if (isSelf)
        {
            VisibilityRow.IsVisible = false;
        }
        else
        {
            VisibilityRow.IsVisible = true;
            VisibilityIcon.Text = isHidden ? "visibility" : "visibility_off";
            VisibilityLabel.Text = isHidden ? "Show on Map" : "Hide from Map";
        }

        // Show the overlay!
        this.IsVisible = true;
    }

    public void Hide()
    {
        this.IsVisible = false;
    }

    private void OnActionTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is string action)
        {
            if (action == "ToggleVisibility")
            {
                action = _isHidden ? "Show on Map" : "Hide from Map";
            }

            Hide();
            ActionSelected?.Invoke(this, action); // Fire the event!
        }
    }

    private void OnCancelTapped(object sender, TappedEventArgs e)
    {
        Hide();
        ActionSelected?.Invoke(this, "Cancel");
    }
}