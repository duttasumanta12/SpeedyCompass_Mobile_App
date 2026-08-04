using Microsoft.Identity.Client;
using Microsoft.Maui.ApplicationModel;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Models;
using System.Collections.ObjectModel;
using System.Net.Http.Json;

namespace SpeedyCompass;

public class GroupItemViewModel
{
    public string GroupName { get; set; }
    public int MemberCount { get; set; }
    public int MaxGroupSize { get; set; }
    public bool IsMyAdmin { get; set; }
    public bool IsMember { get; set; } // Tracks if they already validated the PIN previously

    public string MemberCountDisplay => $"{MemberCount} / {MaxGroupSize} Riders";
    public bool CanJoin => IsMyAdmin || IsMember || MemberCount < MaxGroupSize;

    // Dynamic Button UI Rules
    public string JoinButtonText => IsMyAdmin ? "Resume" : (IsMember ? "Enter" : "Join");
    public Color JoinButtonColor => IsMyAdmin || IsMember ? Colors.DodgerBlue : Colors.MediumSeaGreen;
}

public partial class MainPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly MsalAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;

    private string _pendingJoinGroupName = string.Empty;

    public ObservableCollection<GroupItemViewModel> AvailableGroups { get; set; } = new();

    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);
    private string CurrentUsername => Preferences.Default.Get("username", "Rider");

    public MainPage(SignalRService signalRService, MsalAuthService authService, IHttpClientFactory httpClientFactory)
    {
        InitializeComponent();
        _signalRService = signalRService;
        _authService = authService;
        _httpClientFactory = httpClientFactory;

        GroupsCollectionView.ItemsSource = AvailableGroups;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (DashboardView.IsVisible)
        {
            await _signalRService.StopAsync();
            await LoadGroupsAsync();
            return;
        }

        await AttemptSilentLoginAsync();
    }

    // --- AUTHENTICATION ---
    private async Task AttemptSilentLoginAsync()
    {
        try
        {
            var accounts = await _authService.GetAccounts();
            var firstAccount = accounts.FirstOrDefault();

            if (firstAccount != null)
            {
                ShowLoading("Validating session...");
                var authResult = await _authService.AcquireTokenSilentAsync(firstAccount);

                Preferences.Default.Set("username", authResult.Account.Username);
                Preferences.Default.Set("GoogleId", authResult.UniqueId);

                bool isConnected = await ConnectSignalR(3);
                if (!isConnected)
                {
                    HideLoading();
                    return;
                }

                await ProcessLoginFlow(authResult.UniqueId);
            }
        }
        catch (MsalUiRequiredException) { /* Do nothing, show login UI */ }
        catch (Exception ex) { Console.WriteLine($"Silent Auth failed: {ex.Message}"); }
        finally { HideLoading(); }
    }

    private async void OnAzureLoginClicked(object sender, EventArgs e)
    {
        ShowLoading("Authenticating...");
        try
        {
            var authResult = await _authService.LoginAsync();
            if (authResult == null)
            {
                HideLoading();
                return;
            }

            await ConnectSignalR(3);
            await _signalRService.RegisterOrUpdateUser(authResult.UniqueId, authResult.Account.Username);
            await _signalRService.StopAsync();

            Preferences.Default.Set("username", authResult.Account.Username);
            Preferences.Default.Set("GoogleId", authResult.UniqueId);

            ShowLoading("Connecting to Server...");
            bool flowControl = await ConnectSignalR(3);
            if (!flowControl) return;

            ShowLoading("Fetching Groups...");
            await ProcessLoginFlow(authResult.UniqueId);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Login Failed: {ex.Message}", "OK");
        }
        finally
        {
            HideLoading();
        }
    }
    private async Task ProcessLoginFlow(string googleId)
    {
        await ConnectSignalR(3);
        var profile = await _signalRService.AuthenticateUser(googleId);
        await _signalRService.StopAsync();

        if (profile != null)
        {
            Preferences.Default.Set("username", profile.Username);
            WelcomeNameLabel.Text = profile.Username;

            LoginView.IsVisible = false;
            DashboardView.IsVisible = true;

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
            Preferences.Default.Remove("GoogleId");
            Preferences.Default.Remove("username");
            LoginView.IsVisible = true;
            DashboardView.IsVisible = false;
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
            catch (Exception)
            {
                if (i == maxRetries)
                {
                    HideLoading();
                    await DisplayAlertAsync("Connection Failed", "Could not reach the server. Please check your internet connection.", "OK");
                    return false;
                }
                await Task.Delay(2000);
            }
        }
        return true;
    }

    // --- DASHBOARD DATA LOADING ---
    private async void OnRefreshGroups(object sender, EventArgs e)
    {
        await LoadGroupsAsync();
        GroupsRefreshView.IsRefreshing = false;
    }

    private async Task LoadGroupsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("CompassBackend");
            var googleId = Preferences.Default.Get("GoogleId", "");

            if (string.IsNullOrEmpty(googleId))
            {
                throw new NullReferenceException("GoogleId cannot be null/empty.");
            }

            var groups = await client.GetFromJsonAsync<List<ActiveGroupDto>>($"api/groups?googleId={googleId}") ?? new List<ActiveGroupDto>();

            AvailableGroups.Clear();

            foreach (var g in groups)
            {
                // NOTE FOR BACKEND: Make sure `api/groups` checks if CurrentGoogleId is inside g.ActiveRiders
                // and sets `IsMember` to true in the DTO if they already joined earlier!
                AvailableGroups.Add(new GroupItemViewModel
                {
                    GroupName = g.GroupName,
                    MemberCount = g.MemberCount,
                    MaxGroupSize = g.MaxGroupSize,
                    IsMyAdmin = g.AdminGoogleId == CurrentGoogleId,
                    IsMember = g.IsMember
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching groups: {ex.Message}");
        }
    }

    // --- 1. CREATE GROUP MODAL LOGIC ---
    private void OnOpenCreateGroupModalClicked(object sender, EventArgs e)
    {
        NewGroupNameEntry.Text = string.Empty;
        CreateSizeSlider.Value = 10;
        CreateLagSlider.Value = 500;
        CreateSplinterSlider.Value = 2000;

        CreateGroupModalOverlay.IsVisible = true;
    }

    private void OnCloseCreateGroupModalClicked(object sender, EventArgs e) => CreateGroupModalOverlay.IsVisible = false;

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

    private async void OnConfirmCreateGroupClicked(object sender, EventArgs e)
    {
        string groupName = NewGroupNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(groupName))
        {
            await DisplayAlert("Hold Up", "Please enter a name for your convoy.", "OK");
            return;
        }

        CreateGroupModalOverlay.IsVisible = false;
        ShowLoading("Generating Convoy PIN...");

        // Generate Secure 6-Digit PIN
        string generatedPin = new Random().Next(100000, 999999).ToString();

        // Package the initial settings configured by the Admin
        var initialSettings = new GroupSettingsDto
        {
            MaxGroupSize = (int)Math.Round(CreateSizeSlider.Value),
            MaxLagDistanceMeters = (int)(Math.Round(CreateLagSlider.Value / 50.0) * 50),
            SplinterWarningDistanceMeters = (int)(Math.Round(CreateSplinterSlider.Value / 100.0) * 100),
            PitstopDistanceMeters = 0 // Optional: Add a slider for this if needed
        };

        try
        {
            await ConnectSignalR(3);

            // NOTE FOR BACKEND: Update this SignalR Hub method to accept `generatedPin` and `initialSettings`
            await _signalRService.CreateGroup(groupName, CurrentUsername, CurrentGoogleId, generatedPin, initialSettings);

            var groupDetails = await _signalRService.GetGroupDetails(groupName);

            // Show PIN to Admin before jumping into the Lobby
            await DisplayAlertAsync("Convoy Created! 🏍️", $"Your secure PIN is:\n\n{generatedPin}\n\nShare this with your riders so they can join.", "Let's Ride!");

            HideLoading();

            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
        finally
        {
            HideLoading();
        }
    }

    // --- 2. JOIN GROUP PIN MODAL LOGIC ---
    private async void OnJoinGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is GroupItemViewModel groupData)
        {
            if (groupData.IsMyAdmin || groupData.IsMember)
            {
                // They are already authenticated for this group. Jump straight in!
                await ExecuteJoinFlow(groupData.GroupName, null);
            }
            else
            {
                // They are a new rider trying to join. Ask for the PIN!
                _pendingJoinGroupName = groupData.GroupName;
                JoinPinEntry.Text = string.Empty;
                JoinGroupModalOverlay.IsVisible = true;

                // UX Polish: Auto-focus the keyboard
                JoinPinEntry.Focus();
            }
        }
    }

    private void OnCloseJoinModalClicked(object sender, EventArgs e) => JoinGroupModalOverlay.IsVisible = false;

    private async void OnConfirmJoinPinClicked(object sender, EventArgs e)
    {
        string pinCode = JoinPinEntry.Text?.Trim();
        if (string.IsNullOrEmpty(pinCode) || pinCode.Length != 6)
        {
            await DisplayAlert("Invalid PIN", "Please enter the full 6-digit code provided by the Admin.", "OK");
            return;
        }

        JoinGroupModalOverlay.IsVisible = false;
        await ExecuteJoinFlow(_pendingJoinGroupName, pinCode);
    }

    private async Task ExecuteJoinFlow(string groupName, string pinCode)
    {
        ShowLoading("Joining Convoy...");
        try
        {
            await ConnectSignalR(3);

            // NOTE FOR BACKEND: Update this SignalR Hub method to accept `pinCode`. 
            // If the pinCode is wrong, throw a HubException so it gets caught right here!
            await _signalRService.JoinGroup(groupName, CurrentUsername, CurrentGoogleId, pinCode);

            var groupDetails = await _signalRService.GetGroupDetails(groupName);
            
            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Access Denied", ex.Message, "OK");
            await _signalRService.StopAsync();
        }
        finally
        {
            HideLoading();
        }
    }

    // --- PROFILE MODAL LOGIC ---
    private void OnOpenProfileClicked(object sender, EventArgs e)
    {
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

        KeepScreenOnSwitch.IsToggled = Preferences.Default.Get("KeepScreenOn", false);

        if (isMandatory)
        {
            ProfileModalTitle.Text = "Complete Setup";
            ProfileModalSubtitle.Text = "We need this emergency info before you can ride.";
            CancelProfileButton.IsVisible = false;
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
            await ConnectSignalR(3);
            bool success = await _signalRService.SaveUserProfile(CurrentGoogleId, updatedProfile);
            await _signalRService.StopAsync();

            if (success)
            {
                Preferences.Default.Set("username", updatedProfile.Username);
                Preferences.Default.Set("EmergencyContact", updatedProfile.EmergencyContact);
                Preferences.Default.Set("VehicleNumber", updatedProfile.VehicleNumber);
                Preferences.Default.Set("BloodGroup", updatedProfile.BloodGroup);
                Preferences.Default.Set("HasConsented", updatedProfile.HasConsented);

                Preferences.Default.Set("KeepScreenOn", KeepScreenOnSwitch.IsToggled);
                DeviceDisplay.Current.KeepScreenOn = KeepScreenOnSwitch.IsToggled;

                WelcomeNameLabel.Text = updatedProfile.Username;
                ProfileModalOverlay.IsVisible = false;

                await LoadGroupsAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private void OnCancelProfileClicked(object sender, EventArgs e) => ProfileModalOverlay.IsVisible = false;

    // --- ADMIN ACTION ---
    private async void OnDeleteGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string groupName)
        {
            bool confirm = await DisplayAlert("Delete Group", $"Are you sure you want to delete {groupName}?", "Yes", "No");
            if (confirm)
            {
                await ConnectSignalR(3);
                await _signalRService.DeleteGroup(groupName, CurrentGoogleId);
                await _signalRService.StopAsync();
                await LoadGroupsAsync();
            }
        }
    }
    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Sign Out", "Are you sure you want to log out?", "Yes", "Cancel");
        if (!confirm) return;

        ShowLoading("Signing out...");
        try
        {

            await _authService.LogoutAsync();
           
            // 2. Wipe Local Device Storage (so silent login fails next time)
            Preferences.Default.Remove("GoogleId");
            Preferences.Default.Remove("username");
            Preferences.Default.Remove("EmergencyContact");
            Preferences.Default.Remove("VehicleNumber");
            Preferences.Default.Remove("BloodGroup");
            Preferences.Default.Remove("HasConsented");

            // 3. Clear UI State
            AvailableGroups.Clear();
            ProfileModalOverlay.IsVisible = false;
            DashboardView.IsVisible = false;
            LoginView.IsVisible = true;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Logout Failed: {ex.Message}", "OK");
        }
        finally
        {
            HideLoading();
        }
    }

    private void HideLoading() => MainThread.BeginInvokeOnMainThread(() => LoadingOverlay.IsVisible = false);
    private void ShowLoading(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadingText.Text = message;
            LoadingOverlay.IsVisible = true;
        });
    }
}