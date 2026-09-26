using Microsoft.Maui.ApplicationModel.Communication;

namespace SpeedyCompass;

public partial class ContactUsPage : ContentPage
{
    private const string SupportEmail = "speedycompass@gmail.com";

    public ContactUsPage()
    {
        InitializeComponent();
    }

    private async void OnEmailTapped(object sender, EventArgs e)
    {
        try
        {
            if (Email.Default.IsComposeSupported)
            {
                string subject = "Speedy Compass Link Support";
                var message = new EmailMessage
                {
                    Subject = subject,
                    To = new List<string> { SupportEmail }
                };

                // Opens the native email client (Gmail, Apple Mail, etc.)
                await Email.Default.ComposeAsync(message);
            }
            else
            {
                // Fallback for devices without email clients installed
                await FallbackToClipboard();
            }
        }
        catch (Exception)
        {
            await FallbackToClipboard();
        }
    }

    private async Task FallbackToClipboard()
    {
        await Clipboard.Default.SetTextAsync(SupportEmail);
        await DisplayAlert("Copied", $"We couldn't open your email app. The address {SupportEmail} has been copied to your clipboard.", "OK");
    }
}