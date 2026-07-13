using Android.App;
using Android.Content;
using Microsoft.Identity.Client;

namespace SpeedyCompass.Platforms.Android;

// FIX: This acts as the catcher's mitt for the browser redirect!
// The DataScheme MUST exactly match "msal" + your Client ID
[Activity(Exported = true)]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryBrowsable, Intent.CategoryDefault },
    DataScheme = "msal43f94112-8227-4a0a-95ab-8f7dfbc92177",
    DataHost = "auth")]
public class MsalActivity : BrowserTabActivity
{
    // MSAL's BrowserTabActivity handles the rest automatically!
}