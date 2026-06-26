// MainPage.xaml.cs
using Microsoft.AspNetCore.SignalR.Client;
using SpeedyCompass.Services;

namespace SpeedyCompass;

public partial class MainPage : ContentPage
{
    private readonly SignalRService signalRService;

    public MainPage(SignalRService signalRService)
    {
        InitializeComponent();
        this.signalRService = signalRService;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await signalRService.StartAsync();
    }

    private async void OnCreateGroupClicked(object sender, EventArgs e)
    {
        string groupName = GroupNameEntry.Text;
        string userName = UserNameEntry.Text;

        // 1. Validate with Server
        bool exists = await signalRService.CheckGroupExists(groupName);
        if (exists)
        {
            await DisplayAlert("Error", "This group already exists. Try joining instead.", "OK");
            return;
        }

        // 2. Create and set Admin role
        //await signalRService.CreateGroup(groupName, userName);
        Preferences.Default.Set("username", userName);
        Preferences.Default.Set("IsAdmin", true);

        // 3. Move to Lobby
        await Navigation.PushAsync(new LobbyPage(signalRService, groupName));
    }

    private async void OnJoinGroupClicked(object sender, EventArgs e)
    {
        string groupName = GroupNameEntry.Text;
        string userName = UserNameEntry.Text;

        bool exists = await signalRService.CheckGroupExists(groupName);
        if (!exists)
        {
            await DisplayAlert("Error", "Group not found. Check the name and try again.", "OK");
            return;
        }

        //await signalRService.JoinGroup(groupName, userName);
        Preferences.Default.Set("username", userName);
        Preferences.Default.Set("IsAdmin", false);

        await Navigation.PushAsync(new LobbyPage(signalRService, groupName));
    }
}