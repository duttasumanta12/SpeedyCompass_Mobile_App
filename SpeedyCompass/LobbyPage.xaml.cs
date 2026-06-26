using System.Collections.ObjectModel;
using SpeedyCompass.Services;

namespace SpeedyCompass;

// 1. The local model representing a rider in the UI
public class Rider
{
    public string Name { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = false;

    // Computed properties for UI binding
    public string RoleDisplay => IsAdmin ? "Admin" : "Rider";
    public Color RoleColor => IsAdmin ? Colors.DarkOrange : Colors.Gray;
}

public partial class LobbyPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private bool _hasJoined = false;

    // ObservableCollection automatically updates the UI when items are added/cleared
    public ObservableCollection<Rider> Riders { get; set; } = new();

    public LobbyPage(SignalRService signalRService, string groupName)
    {
        InitializeComponent();
        _signalRService = signalRService;

        // 1. Setup initial UI text
        GroupNameLabel.Text = groupName;
        string myName = Preferences.Default.Get("username", "Unknown");
        bool amIAdmin = Preferences.Default.Get("IsAdmin", false);

        CurrentUserNameLabel.Text = myName;
        CurrentUserRoleLabel.Text = amIAdmin ? "Admin" : "Rider";

        // 2. Toggle UI based on role
        SelectDestinationButton.IsVisible = amIAdmin;
        WaitingForAdminLabel.IsVisible = !amIAdmin;

        // 3. Bind the list to the CollectionView in XAML
        RidersCollectionView.ItemsSource = Riders;

        // 4. Subscribe to the SignalRService events immediately 
        _signalRService.RosterUpdated += OnRosterUpdated;
        _signalRService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _signalRService.NavigationStarted += OnNavigationStarted;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Safety check: Prevent re-joining if the page simply reappears on the navigation stack
        if (_hasJoined) return;

        string myName = Preferences.Default.Get("username", "Unknown");
        bool amIAdmin = Preferences.Default.Get("IsAdmin", false);

        try
        {
            // Call the appropriate hub method based on the user's selected role
            if (amIAdmin)
            {
                await _signalRService.CreateGroup(GroupNameLabel.Text, myName);
            }
            else
            {
                await _signalRService.JoinGroup(GroupNameLabel.Text, myName);
            }

            _hasJoined = true;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Connection Error", $"Could not join group: {ex.Message}", "OK");
            await Navigation.PopAsync(); // Kick them back to the main page if the connection fails
        }
    }

    private void OnRosterUpdated(List<Rider> roster)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            string myName = Preferences.Default.Get("username", "");

            // Clear the old list completely to prevent duplicates
            Riders.Clear();

            // Repopulate with the fresh data from the server
            foreach (var rider in roster)
            {
                // Add a visual indicator for the current user
                if (rider.Name == myName)
                {
                    rider.Name += " (You)";
                }

                Riders.Add(rider);
            }
        });
    }

    private void OnConnectionStatusChanged(string status, Color color)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ConnectionStatusLabel.Text = status;
            ConnectionIndicator.Fill = color;
        });
    }

    private void OnNavigationStarted(double destLat, double destLng, string destName)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var destination = new Location(destLat, destLng);

            // We pass the SignalRService down to the ActiveMapPage as well!
            await Navigation.PushAsync(new ActiveMapPage(_signalRService, GroupNameLabel.Text, destination, destName));
        });
    }

    private async void OnSelectDestinationClicked(object sender, EventArgs e)
    {
        // Navigate to the Map screen to pick a destination
        await Navigation.PushAsync(new DestinationPickerPage(_signalRService, GroupNameLabel.Text));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _signalRService.RosterUpdated -= OnRosterUpdated;
        _signalRService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _signalRService.NavigationStarted -= OnNavigationStarted;
    }
}