namespace SpeedyCompass.Controls;

// 1. A clean data packet to send back to the parent page
public class ProfileSavedEventArgs : EventArgs
{
    public string Username { get; set; }
    public string BloodGroup { get; set; }
    public string EmergencyContact { get; set; }
    public string VehicleNumber { get; set; }
    public bool KeepScreenOn { get; set; }
    public bool HasConsent { get; set; }
}

public partial class ProfileOverlay : ContentView
{
    // Define the custom events the parent page will listen to
    public event EventHandler<ProfileSavedEventArgs> ProfileSaved;
    public event EventHandler LogoutRequested;
    public event EventHandler ProfileClosed;

    public ProfileOverlay()
    {
        InitializeComponent();
    }

    // --- Helper Methods to Control the Component ---

    public void Show(bool isMandatorySetup = false)
    {
        IsVisible = true;

        // If they are a brand new user, hide the cancel button so they MUST fill it out!
        CancelProfileButton.IsVisible = !isMandatorySetup;

        if (isMandatorySetup)
        {
            ProfileModalTitle.Text = "Complete Setup";
            ProfileModalSubtitle.Text = "You must complete your emergency info before riding.";
        }
        else
        {
            ProfileModalTitle.Text = "Rider Profile";
            ProfileModalSubtitle.Text = "Update your emergency and display info.";
        }
    }

    public void Hide()
    {
        IsVisible = false;
    }

    // Call this before calling Show() if you want to pre-fill the user's existing data
    public void LoadData(string username, string bloodGroup, string contact, string vehicle, bool keepScreenOn, bool consent)
    {
        ProfileUsernameEntry.Text = username;
        ProfileContactEntry.Text = contact;
        ProfileVehicleEntry.Text = vehicle;
        KeepScreenOnSwitch.IsToggled = keepScreenOn;
        ConsentCheckbox.IsChecked = consent;

        if (!string.IsNullOrEmpty(bloodGroup) && ProfileBloodGroupPicker.Items.Contains(bloodGroup))
        {
            ProfileBloodGroupPicker.SelectedItem = bloodGroup;
        }
    }

    // --- Button Click Actions ---

    private void OnSaveProfileClicked(object sender, EventArgs e)
    {
        // Add basic validation if you want (e.g. check if Consent is true or Username is empty)
        if (string.IsNullOrWhiteSpace(ProfileUsernameEntry.Text))
        {
            // You could add a Toast or DisplayAlert here!
            return;
        }

        Hide();

        // Package the UI data
        var profileData = new ProfileSavedEventArgs
        {
            Username = ProfileUsernameEntry.Text.Trim(),
            BloodGroup = ProfileBloodGroupPicker.SelectedItem?.ToString() ?? "Unknown",
            EmergencyContact = ProfileContactEntry.Text?.Trim() ?? "",
            VehicleNumber = ProfileVehicleEntry.Text?.Trim() ?? "",
            KeepScreenOn = KeepScreenOnSwitch.IsToggled,
            HasConsent = ConsentCheckbox.IsChecked
        };

        // Send it up to the parent page!
        ProfileSaved?.Invoke(this, profileData);
    }

    private void OnLogoutClicked(object sender, EventArgs e)
    {
        Hide();
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelProfileClicked(object sender, EventArgs e)
    {
        Hide();
        ProfileClosed?.Invoke(this, EventArgs.Empty);
    }
}