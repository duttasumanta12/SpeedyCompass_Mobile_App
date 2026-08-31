using Microsoft.Identity.Client;
using Microsoft.Maui.ApplicationModel;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Models;
using SpeedyCompass.Controls;
using System.Collections.ObjectModel;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SpeedyCompass.Shared;

namespace SpeedyCompass;

public partial class MainPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly MsalAuthService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MainPage> _logger;

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

    private int _currentPage = 1;
    private const int PageSize = 10;
    private string _currentSearchQuery = string.Empty;
    private bool _hasMoreData = true;

    private string CurrentGoogleId => Preferences.Default.Get("GoogleId", string.Empty);
    private string CurrentUsername => Preferences.Default.Get("username", "Rider");

    public MainPage(SignalRService signalRService, MsalAuthService authService, IHttpClientFactory httpClientFactory, ILogger<MainPage> logger)
    {
        InitializeComponent();
        _signalRService = signalRService;
        _authService = authService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        BindingContext = this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] MainPage OnAppearing triggered.", flowId);

        if (DashboardView.IsVisible)
        {
            _logger.LogInformation("[{FlowId}] Dashboard visible, stopping SignalR and reloading groups.", flowId);
            await _signalRService.StopAsync();
            await LoadGroupsAsync(isLoadMore: false, flowId);
            return;
        }

        await AttemptSilentLoginAsync(flowId);
    }

    // ==========================================
    // LOGGING UTILITIES
    // ==========================================
    private string GenerateFlowId() => CorrelationContext.GenerateNew();

    private async Task HandleExceptionAsync(Exception ex, string operationName, string flowId)
    {
        _logger.LogError(ex, "[{FlowId}] Error during {OperationName}.", flowId, operationName);

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await DisplayAlert(
                "System Error",
                $"An unexpected error occurred. Please try again or contact support.\n\nError Code: {flowId}",
                "OK");
        });
    }

    // ==========================================
    // LIST EVENT HANDLERS
    // ==========================================
    private async void OnListRefreshed(object sender, EventArgs e) => await LoadGroupsAsync(isLoadMore: false, GenerateFlowId());

    private async void OnListSearched(object sender, string query)
    {
        _currentSearchQuery = query;
        await LoadGroupsAsync(isLoadMore: false, GenerateFlowId());
    }

    private async void OnListLoadMore(object sender, EventArgs e) => await LoadGroupsAsync(isLoadMore: true, GenerateFlowId());

    private void OnListJoin(object sender, GroupItemViewModel groupData) => OnJoinGroupClicked(this, groupData);

    private void OnListDelete(object sender, string groupName) => OnDeleteGroupClicked(this, groupName);

    // ==========================================
    // PAGINATED DATA LOADER
    // ==========================================
    private async Task LoadGroupsAsync(bool isLoadMore, string flowId)
    {
        _logger.LogInformation("[{FlowId}] Starting LoadGroupsAsync. IsLoadMore: {IsLoadMore}, CurrentPage: {Page}", flowId, isLoadMore, _currentPage);

        if (!isLoadMore)
        {
            _currentPage = 1;
            _hasMoreData = true;
            IsFetchingData = true;

            DynamicEmptyText = string.IsNullOrWhiteSpace(_currentSearchQuery)
                ? "No active convoys right now."
                : $"No convoys found matching '{_currentSearchQuery}'.";
        }
        else
        {
            if (!_hasMoreData) return;
            IsFetchingNextPage = true;
            _currentPage++;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("CompassBackend");
            var googleId = Preferences.Default.Get("GoogleId", "");

            if (string.IsNullOrEmpty(googleId)) throw new NullReferenceException("GoogleId cannot be null/empty.");

            string url = $"api/groups?googleId={googleId}&searchTerm={Uri.EscapeDataString(_currentSearchQuery)}&page={_currentPage}&pageSize={PageSize}";

            _logger.LogInformation("[{FlowId}] Fetching groups from API: {Url}", flowId, url);
            var fetchedGroups = await client.GetFromJsonAsync<List<ActiveGroupDto>>(url) ?? new List<ActiveGroupDto>();

            _logger.LogInformation("[{FlowId}] Successfully fetched {Count} groups.", flowId, fetchedGroups.Count);

            if (!isLoadMore) Groups.Clear();

            if (fetchedGroups.Count < PageSize)
            {
                _logger.LogInformation("[{FlowId}] Reached end of group list data.", flowId);
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
            if (isLoadMore) _currentPage--;
            _logger.LogError(ex, "[{FlowId}] Failed to load groups.", flowId);
        }
        finally
        {
            IsFetchingData = false;
            IsFetchingNextPage = false;
            GroupsListControl.EndRefresh();
            _logger.LogInformation("[{FlowId}] Finished LoadGroupsAsync.", flowId);
        }
    }

    // ==========================================
    // AUTHENTICATION & LOGIN FLOW
    // ==========================================
    private async Task AttemptSilentLoginAsync(string flowId)
    {
        _logger.LogInformation("[{FlowId}] Attempting Silent Auth.", flowId);
        try
        {
            var accounts = await _authService.GetAccounts();
            var firstAccount = accounts.FirstOrDefault();

            if (firstAccount != null)
            {
                _logger.LogInformation("[{FlowId}] Account found in cache. Acquiring token silently.", flowId);
                GlobalLoadingOverlay.Show("Validating session...");
                var authResult = await _authService.AcquireTokenSilentAsync(firstAccount);

                Preferences.Default.Set("username", authResult.Account.Username);
                Preferences.Default.Set("GoogleId", authResult.UniqueId);

                bool isConnected = await ConnectSignalR(3, flowId);
                if (!isConnected)
                {
                    GlobalLoadingOverlay.Hide();
                    return;
                }

                await ProcessLoginFlow(authResult.UniqueId, flowId);
            }
            else
            {
                _logger.LogInformation("[{FlowId}] No account found in cache. User needs to login manually.", flowId);
            }
        }
        catch (MsalUiRequiredException)
        {
            _logger.LogInformation("[{FlowId}] Silent auth failed, interactive login required.", flowId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{FlowId}] Silent Auth encountered an unexpected error.", flowId);
        }
        finally { GlobalLoadingOverlay.Hide(); }
    }

    private async void OnAzureLoginClicked(object sender, EventArgs e)
    {
        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] User clicked Azure Login.", flowId);

        GlobalLoadingOverlay.Show("Authenticating...");
        try
        {
            var authResult = await _authService.LoginAsync();
            if (authResult == null)
            {
                _logger.LogInformation("[{FlowId}] Interactive login cancelled or failed.", flowId);
                return;
            }

            _logger.LogInformation("[{FlowId}] Interactive login successful for {User}.", flowId, authResult.Account.Username);

            await ConnectSignalR(3, flowId);
            await _signalRService.RegisterOrUpdateUser(authResult.UniqueId, authResult.Account.Username);
            await _signalRService.StopAsync();

            Preferences.Default.Set("username", authResult.Account.Username);
            Preferences.Default.Set("GoogleId", authResult.UniqueId);

            GlobalLoadingOverlay.Show("Connecting to Server...");
            bool flowControl = await ConnectSignalR(3, flowId);
            if (!flowControl) return;

            GlobalLoadingOverlay.Show("Fetching Groups...");
            await ProcessLoginFlow(authResult.UniqueId, flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Azure Interactive Login", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async Task ProcessLoginFlow(string googleId, string flowId)
    {
        _logger.LogInformation("[{FlowId}] Processing login data for backend sync.", flowId);

        await ConnectSignalR(3, flowId);
        var profile = await _signalRService.AuthenticateUser(googleId);
        await _signalRService.StopAsync();

        if (profile != null)
        {
            _logger.LogInformation("[{FlowId}] Profile authenticated successfully.", flowId);

            Preferences.Default.Set("username", profile.Username);
            WelcomeNameLabel.Text = profile.Username;

            LoginView.IsVisible = false;
            DashboardView.IsVisible = true;

            if (!profile.HasConsented || string.IsNullOrEmpty(profile.EmergencyContact))
            {
                _logger.LogInformation("[{FlowId}] Profile incomplete. Prompting mandatory setup.", flowId);
                ProfileOverlay.LoadData(profile.Username, profile.BloodGroup, profile.EmergencyContact, profile.VehicleNumber, false, profile.HasConsented);
                ProfileOverlay.Show(isMandatorySetup: true);
            }
            else
            {
                Preferences.Default.Set("EmergencyContact", profile.EmergencyContact);
                Preferences.Default.Set("VehicleNumber", profile.VehicleNumber);
                Preferences.Default.Set("BloodGroup", profile.BloodGroup);
                Preferences.Default.Set("HasConsented", profile.HasConsented);

                await LoadGroupsAsync(isLoadMore: false, flowId);
            }
        }
        else
        {
            _logger.LogWarning("[{FlowId}] Backend returned null profile. Reverting to login view.", flowId);
            Preferences.Default.Remove("GoogleId");
            Preferences.Default.Remove("username");
            LoginView.IsVisible = true;
            DashboardView.IsVisible = false;
        }
    }

    private async Task<bool> ConnectSignalR(int maxRetries, string flowId)
    {
        _logger.LogInformation("[{FlowId}] Starting SignalR Connection...", flowId);

        for (int i = 1; i <= maxRetries; i++)
        {
            try
            {
                if (i > 1) GlobalLoadingOverlay.Show($"Connecting... (Attempt {i}/{maxRetries})");
                await _signalRService.StartAsync();

                _logger.LogInformation("[{FlowId}] SignalR Connected successfully.", flowId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{FlowId}] SignalR Connection Attempt {Attempt} Failed.", flowId, i);
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
    // PROFILE & SETTINGS
    // ==========================================
    private void OnOpenProfileClicked(object sender, EventArgs e)
    {
        _logger.LogInformation("[{FlowId}] User opened Profile UI.", GenerateFlowId());

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
        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] Saving user profile.", flowId);

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
            await ConnectSignalR(3, flowId);
            bool success = await _signalRService.SaveUserProfile(CurrentGoogleId, updatedProfile);
            await _signalRService.StopAsync();

            if (success)
            {
                _logger.LogInformation("[{FlowId}] Profile saved successfully.", flowId);

                Preferences.Default.Set("username", updatedProfile.Username);
                Preferences.Default.Set("EmergencyContact", updatedProfile.EmergencyContact);
                Preferences.Default.Set("VehicleNumber", updatedProfile.VehicleNumber);
                Preferences.Default.Set("BloodGroup", updatedProfile.BloodGroup);
                Preferences.Default.Set("HasConsented", updatedProfile.HasConsented);
                Preferences.Default.Set("KeepScreenOn", e.KeepScreenOn);

                DeviceDisplay.Current.KeepScreenOn = e.KeepScreenOn;
                WelcomeNameLabel.Text = updatedProfile.Username;

                await LoadGroupsAsync(isLoadMore: false, flowId);
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Profile Saving", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async void OnLogoutRequested(object sender, EventArgs e)
    {
        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] User requested logout.", flowId);

        bool confirm = await DisplayAlertAsync("Sign Out", "Are you sure you want to log out?", "Yes", "Cancel");
        if (!confirm) return;

        GlobalLoadingOverlay.Show("Signing out...");
        try
        {
            await _authService.LogoutAsync();
            _logger.LogInformation("[{FlowId}] MSAL Logout successful.", flowId);

            Preferences.Default.Remove("GoogleId");
            Preferences.Default.Remove("username");
            Preferences.Default.Remove("EmergencyContact");
            Preferences.Default.Remove("VehicleNumber");
            Preferences.Default.Remove("BloodGroup");
            Preferences.Default.Remove("HasConsented");

            Groups.Clear();
            DashboardView.IsVisible = false;
            LoginView.IsVisible = true;

            _logger.LogInformation("[{FlowId}] Local preferences cleared. Returned to login view.", flowId);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Logout Flow", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    // ==========================================
    // ADMIN & CONVOY ACTIONS
    // ==========================================
    private async void OnDeleteGroupClicked(object sender, string groupName)
    {
        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] User requested to delete group {GroupName}.", flowId, groupName);

        bool confirm = await DisplayAlert("Delete Group", $"Are you sure you want to delete {groupName}?", "Yes", "No");
        if (confirm)
        {
            try
            {
                await ConnectSignalR(3, flowId);
                await _signalRService.DeleteGroup(groupName, CurrentGoogleId);
                await _signalRService.StopAsync();

                _logger.LogInformation("[{FlowId}] Group {GroupName} deleted successfully.", flowId, groupName);
                await LoadGroupsAsync(isLoadMore: false, flowId);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(ex, "Delete Group", flowId);
            }
        }
    }

    private void OnOpenCreateGroupModalClicked(object sender, EventArgs e)
    {
        _logger.LogInformation("[{FlowId}] User opened Create Group modal.", GenerateFlowId());
        ConvoySettingsOverlay.ShowForCreate();
    }

    private async void OnSettingsSubmitted(object sender, ConvoySettingsSubmittedEventArgs e)
    {
        if (!e.IsCreationMode) return;

        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] Processing new group creation request for {GroupName}.", flowId, e.GroupName);

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
            await ConnectSignalR(3, flowId);

            _logger.LogInformation("[{FlowId}] Submitting creation payload to backend.", flowId);
            await _signalRService.CreateGroup(e.GroupName, CurrentUsername, CurrentGoogleId, generatedPin, initialSettings);

            var groupDetails = await _signalRService.GetGroupDetails(e.GroupName);
            _logger.LogInformation("[{FlowId}] Group created successfully. Routing to LobbyPage.", flowId);

            await DisplayAlertAsync("Convoy Created! 🏍️", $"Your secure PIN is:\n\n{generatedPin}\n\nShare this with your riders so they can join.", "Let's Ride!");

            GlobalLoadingOverlay.Show("Joining Convoy...");
            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Create Group", flowId);
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }

    private async void OnJoinGroupClicked(object sender, GroupItemViewModel groupData)
    {
        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] User requested to join/enter group {GroupName}.", flowId, groupData.GroupName);

        if (groupData.IsMyAdmin || groupData.IsMember)
        {
            await ExecuteJoinFlow(groupData.GroupName, null, flowId);
        }
        else
        {
            JoinGroupOverlay.Show(groupData.GroupName);
        }
    }

    private async void OnJoinConfirmed(object sender, JoinGroupEventArgs e)
    {
        string flowId = GenerateFlowId();
        _logger.LogInformation("[{FlowId}] User confirmed PIN entry for {GroupName}.", flowId, e.GroupName);
        await ExecuteJoinFlow(e.GroupName, e.PinCode, flowId);
    }

    private async Task ExecuteJoinFlow(string groupName, string pinCode, string flowId)
    {
        GlobalLoadingOverlay.Show("Joining Convoy...");
        try
        {
            await ConnectSignalR(3, flowId);

            _logger.LogInformation("[{FlowId}] Executing JoinGroup on SignalR.", flowId);
            await _signalRService.JoinGroup(groupName, CurrentUsername, CurrentGoogleId, pinCode);

            var groupDetails = await _signalRService.GetGroupDetails(groupName);

            _logger.LogInformation("[{FlowId}] Join successful, navigating to LobbyPage.", flowId);
            await Navigation.PushAsync(new LobbyPage(_signalRService, groupDetails));
        }
        catch (Exception ex)
        {
            // Note: If they enter the wrong PIN, we still want to show them the real error message.
            // We only use Correlation IDs for actual system crashes.
            if (ex.Message.Contains("PIN") || ex.Message.Contains("full"))
            {
                _logger.LogInformation("[{FlowId}] Access denied: {Message}", flowId, ex.Message);
                await DisplayAlertAsync("Access Denied", ex.Message, "OK");
            }
            else
            {
                await HandleExceptionAsync(ex, "Join Group Flow", flowId);
            }
            await _signalRService.StopAsync();
        }
        finally
        {
            GlobalLoadingOverlay.Hide();
        }
    }
}