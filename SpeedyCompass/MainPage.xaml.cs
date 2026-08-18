using Microsoft.Identity.Client;
using Microsoft.Maui.ApplicationModel;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Models;
using SpeedyCompass.Controls;
using System.Collections.ObjectModel;
using System.Net.Http.Json;

namespace SpeedyCompass;

public class GroupItemViewModel
{
    public string GroupName { get; set; }
    public int MemberCount { get; set; }
    public int MaxGroupSize { get; set; }
    public bool IsMyAdmin { get; set; }
    public bool IsMember { get; set; }

    public string MemberCountDisplay => $"{MemberCount} / {MaxGroupSize} Riders";
    public bool CanJoin => IsMyAdmin || IsMember || MemberCount < MaxGroupSize;

    public string JoinButtonText => IsMyAdmin ? "Resume" : (IsMember ? "Enter" : "Join");
    public Color JoinButtonColor => IsMyAdmin || IsMember ? Colors.DodgerBlue : Colors.MediumSeaGreen;
}

public partial class MainPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly MsalAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;

    // ==========================================
    // 1. DATA BINDINGS FOR THE LIST UI
    // ==========================================
    public ObservableCollection<GroupItemViewModel> Groups { get; set; } = new();

    private bool _isFetchingData;
    public bool IsFetchingData
    {
        get => _isFetchingData;
        set { _isFetchingData = value; OnPropertyChanged(); }
    }

    private bool _isFetchingNextPage;
    public bool IsFetchingNextPage
    {
        get => _isFetchingNextPage;
        set { _isFetchingNextPage = value; OnPropertyChanged(); }
    }

    private string _dynamicEmptyText = "No active convoys right now.";
    public string DynamicEmptyText
    {
        get => _dynamicEmptyText;
        set { _dynamicEmptyText = value; OnPropertyChanged(); }
    }

    // --- Pagination State ---
    private int _currentPage = 1;
    private const int PageSize = 10;
    private string _currentSearchQuery = string.Empty;
    private bool _hasMoreData = true;

    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);
    private string CurrentUsername => Preferences.Default.Get("username", "Rider");

    public MainPage(SignalRService signalRService, MsalAuthService authService, IHttpClientFactory httpClientFactory)
    {
        InitializeComponent();
        _signalRService = signalRService;
        _authService = authService;
        _httpClientFactory = httpClientFactory;

        // THE FIX: Set the BindingContext so the XAML bindings work!
        BindingContext = this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (DashboardView.IsVisible)
        {
            await _signalRService.StopAsync();
            await LoadGroupsAsync(isLoadMore: false);
            return;
        }

        await AttemptSilentLoginAsync();
    }

    // ==========================================
    // 2. LIST EVENT HANDLERS (From the Component)
    // ==========================================
    private async void OnListRefreshed(object sender, EventArgs e)
    {
        await LoadGroupsAsync(isLoadMore: false);
    }

    private async void OnListSearched(object sender, string query)
    {
        _currentSearchQuery = query;
        await LoadGroupsAsync(isLoadMore: false);
    }

    private async void OnListLoadMore(object sender, EventArgs e)
    {
        await LoadGroupsAsync(isLoadMore: true);
    }

    private void OnListJoin(object sender, GroupItemViewModel groupData)
    {
        OnJoinGroupClicked(this, groupData);
    }

    private void OnListDelete(object sender, string groupName)
    {
        OnDeleteGroupClicked(this, groupName);
    }


    // ==========================================
    // 3. PAGINATED DATA LOADER
    // ==========================================
    private async Task LoadGroupsAsync(bool isLoadMore = false)
    {
        if (!isLoadMore)
        {
            _currentPage = 1;
            _hasMoreData = true;
            IsFetchingData = true; // Shows the full-screen overlay

            // Update the empty message depending on if they are searching
            DynamicEmptyText = string.IsNullOrWhiteSpace(_currentSearchQuery)
                ? "No active convoys right now."
                : $"No convoys found matching '{_currentSearchQuery}'.";
        }
        else
        {
            if (!_hasMoreData) return;
            IsFetchingNextPage = true; // Shows the footer spinner
            _currentPage++;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("CompassBackend");
            var googleId = Preferences.Default.Get("GoogleId", "");

            if (string.IsNullOrEmpty(googleId)) throw new NullReferenceException("GoogleId cannot be null/empty.");

            // -------------------------------------------------------------------------
            // THE FIX: Pass Search & Pagination info to your ASP.NET Backend
            // You will need to update your Backend Controller to accept these variables!
            // -------------------------------------------------------------------------
            string url = $"api/groups?googleId={googleId}&searchTerm={Uri.EscapeDataString(_currentSearchQuery)}&page={_currentPage}&pageSize={PageSize}";
            var fetchedGroups = await client.GetFromJsonAsync<List<ActiveGroupDto>>(url) ?? new List<ActiveGroupDto>();

            if (!isLoadMore)
            {
                Groups.Clear();
            }

            // If the backend returned fewer items than the page size, we hit the end of the list
            if (fetchedGroups.Count < PageSize)
            {
                _hasMoreData = false;
            }

            foreach (var g in fetchedGroups)
            {
                Groups.Add(new GroupItemViewModel
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
            if (isLoadMore) _currentPage--; // Roll back the page count if it failed to load
        }
        finally
        {
            IsFetchingData = false;
            IsFetchingNextPage = false;
            GroupsListControl.EndRefresh();
        }
    }


    // ==========================================
    // 4. AUTHENTICATION & LOGIN FLOW
    // ==========================================
    private async Task AttemptSilentLoginAsync()
    {
        try
        {
            var accounts = await _authService.GetAccounts();
            var firstAccount = accounts.FirstOrDefault();

            if (firstAccount != null)
            {
                GlobalLoadingOverlay.Show("Validating session...");
                var authResult = await _authService.AcquireTokenSilentAsync(firstAccount);

                Preferences.Default.Set("username", authResult.Account.Username);
                Preferences.Default.Set("GoogleId", authResult.UniqueId);

                bool isConnected = await ConnectSignalR(3);
                if (!isConnected)
                {
                    GlobalLoadingOverlay.Hide();
                    return;
                }

                await ProcessLoginFlow(authResult.UniqueId);
            }
        }
        catch (MsalUiRequiredException) { /* Do nothing, show login UI */ }
        catch (Exception ex) { Console.WriteLine($"Silent Auth failed: {ex.Message}"); }
        finally { GlobalLoadingOverlay.Hide(); }
    }

    private async void OnAzureLoginClicked(object sender, EventArgs e)
    {
        GlobalLoadingOverlay.Show("Authenticating...");
        try
        {
            var authResult = await _authService.LoginAsync();
            if (authResult == null)
            {
                GlobalLoadingOverlay.Hide();
                return;
            }

            await ConnectSignalR(3);
            await _signalRService.RegisterOrUpdateUser(authResult.UniqueId, authResult.Account.Username);
            await _signalRService.StopAsync();

            Preferences.Default.Set("username", authResult.Account.Username);
            Preferences.Default.Set("GoogleId", authResult.UniqueId);

            GlobalLoadingOverlay.Show("Connecting to Server...");
            bool flowControl = await ConnectSignalR(3);
            if (!flowControl) return;

            GlobalLoadingOverlay.Show("Fetching Groups...");
            await ProcessLoginFlow(authResult.UniqueId);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Login Failed: {ex.Message}", "OK");
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
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
                ProfileOverlay.LoadData(profile.Username, profile.BloodGroup, profile.EmergencyContact, profile.VehicleNumber, false, profile.HasConsented);
                ProfileOverlay.Show(isMandatorySetup: true);
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
                if (i > 1) GlobalLoadingOverlay.Show($"Connecting... (Attempt {i}/{maxRetries})");
                await _signalRService.StartAsync();
                return true;
            }
            catch (Exception)
            {
                if (i == maxRetries)
                {
                    GlobalLoadingOverlay.Hide();
                    await DisplayAlertAsync("Connection Failed", "Could not reach the server. Please check your internet connection.", "OK");
                    return false;
                }
                await Task.Delay(2000);
            }
        }
        return true;
    }

    // ==========================================
    // 5. PROFILE & SETTINGS
    // ==========================================
    private void OnOpenProfileClicked(object sender, EventArgs e)
    {
        ProfileOverlay.LoadData(
            username: Preferences.Default.Get("username", "Rider"),
            bloodGroup: Preferences.Default.Get("BloodGroup", "Unknown"),
            contact: Preferences.Default.Get("EmergencyContact", ""),
            vehicle: Preferences.Default.Get("VehicleNumber", ""),
            keepScreenOn: Preferences.Default.Get("KeepScreenOn", false),
            consent: Preferences.Default.Get("HasConsented", true)
        );

        ProfileOverlay.Show(isMandatorySetup: false);
    }

    private async void OnProfileSaved(object sender, ProfileSavedEventArgs e)
    {
        GlobalLoadingOverlay.Show("Saving profile...");

        var updatedProfile = new UserProfileDto
        {
            Username = e.Username,
            EmergencyContact = e.EmergencyContact,
            VehicleNumber = e.VehicleNumber,
            BloodGroup = e.BloodGroup,
            HasConsented = e.HasConsent
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
                Preferences.Default.Set("KeepScreenOn", e.KeepScreenOn);

                DeviceDisplay.Current.KeepScreenOn = e.KeepScreenOn;
                WelcomeNameLabel.Text = updatedProfile.Username;

                await LoadGroupsAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async void OnLogoutRequested(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Sign Out", "Are you sure you want to log out?", "Yes", "Cancel");
        if (!confirm) return;

        GlobalLoadingOverlay.Show("Signing out...");
        try
        {
            await _authService.LogoutAsync();

            Preferences.Default.Remove("GoogleId");
            Preferences.Default.Remove("username");
            Preferences.Default.Remove("EmergencyContact");
            Preferences.Default.Remove("VehicleNumber");
            Preferences.Default.Remove("BloodGroup");
            Preferences.Default.Remove("HasConsented");

            Groups.Clear();
            DashboardView.IsVisible = false;
            LoginView.IsVisible = true;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Logout Failed: {ex.Message}", "OK");
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    // ==========================================
    // 6. ADMIN & CONVOY ACTIONS
    // ==========================================
    private async void OnDeleteGroupClicked(object sender, string groupName)
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

    private void OnOpenCreateGroupModalClicked(object sender, EventArgs e)
    {
        ConvoySettingsOverlay.ShowForCreate();
    }

    private async void OnSettingsSubmitted(object sender, ConvoySettingsSubmittedEventArgs e)
    {
        if (!e.IsCreationMode) return;

        GlobalLoadingOverlay.Show("Generating Convoy PIN...");

        string generatedPin = new Random().Next(100000, 999999).ToString();
        var initialSettings = new GroupSettingsDto
        {
            MaxGroupSize = e.MaxGroupSize,
            MaxLagDistanceMeters = e.MaxLagDistanceMeters,
            SplinterWarningDistanceMeters = e.SplinterWarningDistanceMeters,
            PitstopDistanceMeters = e.PitstopDistanceMeters,
            EnableDynamicRouting = e.EnableDynamicRouting,
            MinUpdateDistanceMeters = e.MinBroadcastDistanceMeters,
            MaxUpdateDistanceMeters = e.MaxBroadcastDistanceMeters
        };

        try
        {
            await ConnectSignalR(3);
            await _signalRService.CreateGroup(e.GroupName, CurrentUsername, CurrentGoogleId, generatedPin, initialSettings);
            var groupDetails = await _signalRService.GetGroupDetails(e.GroupName);

            await DisplayAlertAsync("Convoy Created! 🏍️", $"Your secure PIN is:\n\n{generatedPin}\n\nShare this with your riders so they can join.", "Let's Ride!");

            GlobalLoadingOverlay.Show("Joining Convoy...");
            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async void OnJoinGroupClicked(object sender, GroupItemViewModel groupData)
    {
        if (groupData.IsMyAdmin || groupData.IsMember)
        {
            await ExecuteJoinFlow(groupData.GroupName, null);
        }
        else
        {
            JoinGroupOverlay.Show(groupData.GroupName);
        }
    }

    private async void OnJoinConfirmed(object sender, JoinGroupEventArgs e)
    {
        await ExecuteJoinFlow(e.GroupName, e.PinCode);
    }

    private async Task ExecuteJoinFlow(string groupName, string pinCode)
    {
        GlobalLoadingOverlay.Show("Joining Convoy...");
        try
        {
            await ConnectSignalR(3);
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
            GlobalLoadingOverlay.Hide();
        }
    }
}