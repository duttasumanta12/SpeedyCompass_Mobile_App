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
    public ObservableCollection<GroupItemViewModel> AvailableGroups { get; set; } = new();

    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);

    public MainPage(SignalRService signalRService)
    {
        InitializeComponent();
        _signalRService = signalRService;
        GroupsCollectionView.ItemsSource = AvailableGroups;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Start connection first so we can talk to the hub
        await _signalRService.StartAsync();

        if (!string.IsNullOrEmpty(CurrentGoogleId))
        {
            // Validate our saved GoogleId with the Server
            string serverUsername = await _signalRService.AuthenticateUser(CurrentGoogleId);

            if (!string.IsNullOrEmpty(serverUsername))
            {
                Preferences.Default.Set("username", serverUsername);
                CheckLoginState();
                await LoadGroupsAsync();
            }
            else
            {
                // The server restarted and forgot our ID, or it's invalid. Reset local state.
                Preferences.Default.Remove("GoogleId");
                Preferences.Default.Remove("username");
                LoginView.IsVisible = true;
                DashboardView.IsVisible = false;
            }
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

    private async void OnGoogleLoginClicked(object sender, EventArgs e)
    {
        try
        {
            // Default Google first name
            string desiredName = "John";

            // Ask server to generate GoogleId and reserve the username
            string newGoogleId = await _signalRService.RegisterOrUpdateUser(string.Empty, desiredName);

            Preferences.Default.Set("GoogleId", newGoogleId);
                Preferences.Default.Set("username", desiredName);

                CheckLoginState();
                await LoadGroupsAsync();
            
        }
        catch (Exception fallbackEx)
        {
            // Fallback: If "John" is already taken by someone else on the server, append a random number
            try
            {
                string fallbackName = "John" + new Random().Next(1000, 9999);
                string newGoogleId = await _signalRService.RegisterOrUpdateUser(string.Empty, fallbackName);

                Preferences.Default.Set("GoogleId", newGoogleId);
                Preferences.Default.Set("username", fallbackName);

                CheckLoginState();
                await LoadGroupsAsync();

                await DisplayAlert("Notice", $"Your default username was taken. You have been assigned '{fallbackName}'. You can change this in the dashboard.", "OK");
            }
            catch (Exception fallbackEx2)
            {
                await DisplayAlert("Login Error", fallbackEx2.Message, "OK");
            }
        }
    }

    private async void OnSaveUsernameClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UsernameEntry.Text)) return;

        string originalUsername = Preferences.Default.Get("username", string.Empty);

        try
        {
            // Attempt to claim the new username on the server using our existing GoogleId
            await _signalRService.RegisterOrUpdateUser(CurrentGoogleId, UsernameEntry.Text.Trim());

            Preferences.Default.Set("username", UsernameEntry.Text.Trim());
            await DisplayAlert("Saved", "Username updated successfully.", "OK");
        }
        catch (Exception ex)
        {
            // The server rejected it (likely because it's taken). Revert the Entry box.
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
                // Check if the user is the admin of the group they are trying to enter
                var groupInfo = AvailableGroups.FirstOrDefault(g => g.GroupName == groupName);
                bool amIAdmin = groupInfo?.IsMyAdmin ?? false;

                await _signalRService.JoinGroup(groupName, UsernameEntry.Text, CurrentGoogleId);

                // Set the admin preference correctly so the Lobby grants them the right controls
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