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

        await _signalRService.StartAsync();

        if (!string.IsNullOrEmpty(CurrentGoogleId))
        {
            await ProcessLoginFlow(CurrentGoogleId);
        }
        else
        {
            LoginView.IsVisible = true;
            DashboardView.IsVisible = false;
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
        try
        {
            var authResult = await _authService.LoginAsync();
            if (authResult != null)
            {
                string azureId = authResult.UniqueId;
                string desiredName = authResult.Account.Username ?? "Rider";

                if (desiredName.Contains("@")) desiredName = desiredName.Split('@')[0];

                // For a brand new user, we create a default DTO and save it so they exist in CosmosDB
                //var newProfile = new UserProfileDto { Username = desiredName, HasConsented = false };
                //await _signalRService.SaveUserProfile(azureId, newProfile);

                Preferences.Default.Set("GoogleId", azureId);

                await ProcessLoginFlow(azureId);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Login Error", ex.Message, "OK");
        }
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
            Preferences.Default.Set("IsAdmin", true);
            await Navigation.PushAsync(new LobbyPage(_signalRService, groupName));
        }
        catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
    }

    private async void OnJoinGroupClicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string groupName)
        {
            try
            {
                var groupInfo = AvailableGroups.FirstOrDefault(g => g.GroupName == groupName);
                bool amIAdmin = groupInfo?.IsMyAdmin ?? false;

                await _signalRService.JoinGroup(groupName, WelcomeNameLabel.Text, CurrentGoogleId);

                Preferences.Default.Set("IsAdmin", amIAdmin);
                await Navigation.PushAsync(new LobbyPage(_signalRService, groupName));
            }
            catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
        }
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