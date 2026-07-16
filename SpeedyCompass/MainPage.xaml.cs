using Microsoft.Identity.Client;
using Microsoft.Maui.ApplicationModel;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Models;
using System.Collections.ObjectModel;

namespace SpeedyCompass;

public class GroupItemViewModel
{
    public string GroupName { get; set; }
    public int MemberCount { get; set; }
    public int MaxGroupSize { get; set; }
    public bool IsMyAdmin { get; set; }
    public string MemberCountDisplay => $"{MemberCount} / {MaxGroupSize} Members";
    public bool CanJoin => IsMyAdmin || MemberCount < MaxGroupSize;
    public string JoinButtonText => IsMyAdmin ? "Enter" : "Join";
}

public partial class MainPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly MsalAuthService _authService;

    public ObservableCollection<GroupItemViewModel> AvailableGroups { get; set; } = new();

    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);

    public MainPage(SignalRService signalRService, MsalAuthService authService)
    {
        InitializeComponent();
        _signalRService = signalRService;
        _authService = authService;
        GroupsCollectionView.ItemsSource = AvailableGroups;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 🛡️ THE GUARD CLAUSE 🛡️
        // If the Dashboard is already visible, it means we already successfully logged in
        // and connected to SignalR during this app session. 
        if (DashboardView.IsVisible)
        {
            // Just silently refresh the group list in the background and exit!
            // No new tokens, no new SignalR connections.
            await LoadGroupsAsync();
            return;
        }

        // If we reach here, it's a fresh boot. Attempt the silent token validation!
        await AttemptSilentLoginAsync();
    }
    private async Task AttemptSilentLoginAsync()
    {
        try
        {
            // Fetch accounts from the MSAL cache (This is your local token cache!)
            var accounts = await _authService.GetAccounts();
            var firstAccount = accounts.FirstOrDefault();

            if (firstAccount != null)
            {
                ShowLoading("Validating session...");

                // AcquireTokenSilent automatically checks if the cached token is valid.
                // If it's expired, MSAL automatically uses the refresh token to get a new one!
                var authResult = await _authService.AcquireTokenSilentAsync(firstAccount);

                // Save credentials securely
                Preferences.Default.Set("username", authResult.Account.Username);
                Preferences.Default.Set("GoogleId", authResult.UniqueId);

                bool isConnected = false;
                isConnected = await ConnectSignalR(3);

                if(!isConnected)
                {
                    HideLoading();
                    return; // Stop the flow completely
                }

                await ProcessLoginFlow(authResult.UniqueId); // Proceed with the login flow using the valid token
            }
        }
        catch (MsalUiRequiredException)
        {
            // The token is completely expired, or the user changed their password.
            // The cache is invalid. Do nothing and let them see the "Login" button.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Silent Auth failed: {ex.Message}");
        }
        finally
        {
            HideLoading();
        }
    }

    private async Task ProcessLoginFlow(string googleId)
    {
        // Fetch full profile from backend
        var profile = await _signalRService.AuthenticateUser(googleId);

        if (profile != null)
        {
            Preferences.Default.Set("username", profile.Username);
            WelcomeNameLabel.Text = profile.Username;

            LoginView.IsVisible = false;
            DashboardView.IsVisible = true;

            // MANDATORY CHECK: Have they filled out the emergency profile?
            if (!profile.HasConsented || string.IsNullOrEmpty(profile.EmergencyContact))
            {
                OpenProfileModal(profile, isMandatory: true);
            }
            else
            {
                Preferences.Default.Set("EmergencyContact", profile.EmergencyContact);
                Preferences.Default.Set("VehicleNumber", profile.VehicleNumber);
                Preferences.Default.Set("BloodGroup", profile.BloodGroup);
                Preferences.Default.Set("HasConsented", profile.HasConsented);

                await LoadGroupsAsync();
            }
        }
        else
        {
            // Invalid session, dump to login
            Preferences.Default.Remove("GoogleId");
            Preferences.Default.Remove("username");
            LoginView.IsVisible = true;
            DashboardView.IsVisible = false;
        }
    }

    private async void OnAzureLoginClicked(object sender, EventArgs e)
    {
        // 1. Lock UI and Authenticate via Azure B2C
        ShowLoading("Authenticating...");

        try
        {
            var authResult = await _authService.LoginAsync();
            if (authResult == null)
            {
                HideLoading();
                return; // User canceled or login failed
            }

            // Save credentials securely
            Preferences.Default.Set("username", authResult.Account.Username);
            Preferences.Default.Set("GoogleId", authResult.UniqueId);

            // 2. ROBUST INITIAL CONNECTION WITH RETRY LOGIC
            ShowLoading("Connecting to Server...");
            int maxRetries = 3;
            bool flowControl = await ConnectSignalR(maxRetries);
            if (!flowControl)
            {
                return;
            }

            // 3. Fetch Active Groups using the now-open socket!
            ShowLoading("Fetching Groups...");

            await ProcessLoginFlow(authResult.UniqueId);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Login Failed: {ex.Message}", "OK");
        }
        finally
        {
            HideLoading();
        }
    }

    private async Task<bool> ConnectSignalR(int maxRetries)
    {
        for (int i = 1; i <= maxRetries; i++)
        {
            try
            {
                if (i > 1) ShowLoading($"Connecting... (Attempt {i}/{maxRetries})");

                await _signalRService.StartAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SignalR Start Failed (Attempt {i}): {ex.Message}");

                if (i == maxRetries)
                {
                    HideLoading();
                    await DisplayAlert("Connection Failed", "Could not reach the server. Please check your internet connection and try again.", "OK");
                    return false; // Stop the flow completely
                }

                await Task.Delay(2000); // Wait 2 seconds before retrying
            }
        }

        return true;
    }

    // --- PROFILE MODAL LOGIC ---
    private void OnOpenProfileClicked(object sender, EventArgs e)
    {
        // Opening manually from the Dashboard -> Pre-fill with known preferences and allow canceling
        var profile = new UserProfileDto
        {
            Username = Preferences.Default.Get("username", "Rider"),
            EmergencyContact = Preferences.Default.Get("EmergencyContact", ""),
            VehicleNumber = Preferences.Default.Get("VehicleNumber", ""),
            BloodGroup = Preferences.Default.Get("BloodGroup", "Unknown"),
            HasConsented = true
        };
        OpenProfileModal(profile, isMandatory: false);
    }

    private void OpenProfileModal(UserProfileDto profile, bool isMandatory)
    {
        ProfileUsernameEntry.Text = profile.Username;
        ProfileContactEntry.Text = profile.EmergencyContact;
        ProfileVehicleEntry.Text = profile.VehicleNumber;
        ProfileBloodGroupPicker.SelectedItem = string.IsNullOrEmpty(profile.BloodGroup) ? "Unknown" : profile.BloodGroup;
        ConsentCheckbox.IsChecked = profile.HasConsented;

        // NEW: Load Screen On Preference
        KeepScreenOnSwitch.IsToggled = Preferences.Default.Get("KeepScreenOn", false);

        if (isMandatory)
        {
            ProfileModalTitle.Text = "Complete Setup";
            ProfileModalSubtitle.Text = "We need this emergency info before you can ride.";
            CancelProfileButton.IsVisible = false; // Force them to finish
        }
        else
        {
            ProfileModalTitle.Text = "Edit Profile";
            ProfileModalSubtitle.Text = "Update your emergency info.";
            CancelProfileButton.IsVisible = true;
        }

        ProfileModalOverlay.IsVisible = true;
    }

    private async void OnSaveProfileClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileUsernameEntry.Text) || string.IsNullOrWhiteSpace(ProfileContactEntry.Text))
        {
            await DisplayAlert("Missing Info", "Username and Emergency Contact are required fields.", "OK");
            return;
        }

        if (!ConsentCheckbox.IsChecked)
        {
            await DisplayAlert("Consent Required", "You must agree to the data storage policy to use the safety features.", "OK");
            return;
        }

        var updatedProfile = new UserProfileDto
        {
            Username = ProfileUsernameEntry.Text.Trim(),
            EmergencyContact = ProfileContactEntry.Text.Trim(),
            VehicleNumber = ProfileVehicleEntry.Text?.Trim() ?? "",
            BloodGroup = ProfileBloodGroupPicker.SelectedItem?.ToString() ?? "Unknown",
            HasConsented = true
        };

        try
        {
            bool success = await _signalRService.SaveUserProfile(CurrentGoogleId, updatedProfile);
            if (success)
            {
                Preferences.Default.Set("username", updatedProfile.Username);
                Preferences.Default.Set("EmergencyContact", updatedProfile.EmergencyContact);
                Preferences.Default.Set("VehicleNumber", updatedProfile.VehicleNumber);
                Preferences.Default.Set("BloodGroup", updatedProfile.BloodGroup);

                // NEW: Save and Apply Screen On Preference immediately
                Preferences.Default.Set("KeepScreenOn", KeepScreenOnSwitch.IsToggled);
                DeviceDisplay.Current.KeepScreenOn = KeepScreenOnSwitch.IsToggled;

                WelcomeNameLabel.Text = updatedProfile.Username;
                ProfileModalOverlay.IsVisible = false;

                await LoadGroupsAsync(); // Load groups now that they are authorized
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private void OnCancelProfileClicked(object sender, EventArgs e)
    {
        ProfileModalOverlay.IsVisible = false;
    }

    // --- GROUP LOGIC ---
    private async void OnRefreshGroups(object sender, EventArgs e)
    {
        await LoadGroupsAsync();
        GroupsRefreshView.IsRefreshing = false;
    }

    private async Task LoadGroupsAsync()
    {
        try
        {
            var groups = await _signalRService.GetActiveGroups();
            AvailableGroups.Clear();
            foreach (var g in groups)
            {
                AvailableGroups.Add(new GroupItemViewModel
                {
                    GroupName = g.GroupName,
                    MemberCount = g.MemberCount,
                    MaxGroupSize = g.MaxGroupSize,
                    IsMyAdmin = g.AdminGoogleId == CurrentGoogleId
                });
            }
        }
        catch (Exception ex) { Console.WriteLine($"Failed to load groups: {ex.Message}"); }
    }

    private async void OnCreateGroupClicked(object sender, EventArgs e)
    {
        string groupName = GroupNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(groupName)) return;

        try
        {
            await _signalRService.CreateGroup(groupName, WelcomeNameLabel.Text, CurrentGoogleId);
            var groupDetails = await _signalRService.GetGroupDetails(groupName);

            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
    }

    private async void OnJoinGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string groupName)
        {
        }
        else
        {
            return;
        }
        string userName = Preferences.Default.Get("username", "Rider");

        if (string.IsNullOrEmpty(groupName))
        {
            await DisplayAlert("Error", "Please select or enter a group to join.", "OK");
            return;
        }

        // Lock the UI
        ShowLoading("Joining Convoy...");

        try
        {
            // Note: We know SignalR is ALREADY connected here from OnAzureLoginClicked!
            await _signalRService.JoinGroup(groupName, userName, CurrentGoogleId);

            var groupDetails = await _signalRService.GetGroupDetails(groupName);

            // Navigate to Lobby
            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Connection Failed", ex.Message, "OK");
        }
        finally
        {
            HideLoading();
        }
    }

    private void HideLoading()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadingOverlay.IsVisible = false;
        });
    }
    private void ShowLoading(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadingText.Text = message;
            LoadingOverlay.IsVisible = true;
        });
    }

    private async void OnDeleteGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string groupName)
        {
            bool confirm = await DisplayAlert("Delete Group", $"Are you sure you want to delete {groupName}?", "Yes", "No");
            if (confirm)
            {
                await _signalRService.DeleteGroup(groupName, CurrentGoogleId);
                await LoadGroupsAsync();
            }
        }
    }
}