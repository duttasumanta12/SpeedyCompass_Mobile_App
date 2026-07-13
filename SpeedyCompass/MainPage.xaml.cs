using System.Collections.ObjectModel;
using Microsoft.Maui.ApplicationModel;
using SpeedyCompass.Services;

namespace SpeedyCompass;

public class GroupItemViewModel
{
    public string GroupName { get; set; }
    public int MemberCount { get; set; }
    public bool IsMyAdmin { get; set; }
    public string MemberCountDisplay => $"{MemberCount} / 5 Members";

    // Admin can always attempt to re-enter their own group
    public bool CanJoin => IsMyAdmin || MemberCount < 5;

    // Dynamically change the button text
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
            string serverUsername = await _signalRService.AuthenticateUser(CurrentGoogleId);

            if (!string.IsNullOrEmpty(serverUsername))
            {
                Preferences.Default.Set("username", serverUsername);
                CheckLoginState();
                await LoadGroupsAsync();
            }
            else
            {
                Preferences.Default.Remove("GoogleId");
                Preferences.Default.Remove("username");
                LoginView.IsVisible = true;
                DashboardView.IsVisible = false;
            }
        }
        else
        {
            CheckLoginState();
        }
    }

    private void CheckLoginState()
    {
        if (!string.IsNullOrEmpty(CurrentGoogleId))
        {
            LoginView.IsVisible = false;
            DashboardView.IsVisible = true;
            UsernameEntry.Text = Preferences.Default.Get("username", "Rider");
        }
    }

    // UPDATED: Now handles standard Azure AD Registration/Login!
    private async void OnAzureLoginClicked(object sender, EventArgs e)
    {
        try
        {
            // 1. Launch the Azure AD B2C Login/Register Browser
            var authResult = await _authService.LoginAsync();

            if (authResult != null)
            {
                string azureId = authResult.UniqueId;
                string desiredName = authResult.Account.Username ?? "Rider";

                // If they signed up with email, parse the prefix to make a clean default username
                if (desiredName.Contains("@"))
                {
                    desiredName = desiredName.Split('@')[0];
                }

                string registeredId = await _signalRService.RegisterOrUpdateUser(azureId, desiredName);

                Preferences.Default.Set("GoogleId", registeredId); // We keep the local key name "GoogleId" to avoid breaking existing DB logic
                Preferences.Default.Set("username", desiredName);

                CheckLoginState();
                await LoadGroupsAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Login Error", ex.Message, "OK");
        }
    }

    private async void OnSaveUsernameClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UsernameEntry.Text)) return;

        string originalUsername = Preferences.Default.Get("username", string.Empty);

        try
        {
            await _signalRService.RegisterOrUpdateUser(CurrentGoogleId, UsernameEntry.Text.Trim());
            Preferences.Default.Set("username", UsernameEntry.Text.Trim());
            await DisplayAlert("Saved", "Username updated successfully.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
            UsernameEntry.Text = originalUsername;
        }
    }

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
            await _signalRService.CreateGroup(groupName, UsernameEntry.Text, CurrentGoogleId);
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

                await _signalRService.JoinGroup(groupName, UsernameEntry.Text, CurrentGoogleId);

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
                await _signalRService.DeleteGroup(groupName);
                await LoadGroupsAsync();
            }
        }
    }
}