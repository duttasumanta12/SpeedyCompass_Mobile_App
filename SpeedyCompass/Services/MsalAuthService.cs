using Microsoft.Identity.Client;
using System.Collections;
using System.Diagnostics;

namespace SpeedyCompass.Services;

public class MsalAuthService
{
    private readonly IPublicClientApplication _pca;

    // IMPORTANT: Ensure these match your Azure AD B2C configuration
    private const string ClientId = "43f94112-8227-4a0a-95ab-8f7dfbc92177";
    private const string TenantName = "speedycompass";
    private const string TenantId = $"{TenantName}.onmicrosoft.com";
    private const string PolicySignUpSignIn = "B2C_1_SpeedyCompassSigninSignup";

    private readonly string[] Scopes = { "openid", "offline_access" };

    public MsalAuthService()
    {
        // Construct the B2C authority URL
        var authority = $"https://{TenantName}.b2clogin.com/tfp/{TenantId}/{PolicySignUpSignIn}/";

        _pca = PublicClientApplicationBuilder.Create(ClientId)
            .WithB2CAuthority(authority)
            .WithRedirectUri($"msal{ClientId}://auth")
            // Inject the custom SSL bypass handler for local development
            //.WithHttpClientFactory(new CustomMsalHttpClientFactory())
            .Build();
    }

    public async Task<AuthenticationResult> LoginAsync()
    {
        try
        {
            // 1. Try to authenticate silently (if the user logged in previously)
            var accounts = await _pca.GetAccountsAsync();
            var firstAccount = accounts.FirstOrDefault();

            return await _pca.AcquireTokenSilent(Scopes, firstAccount).ExecuteAsync();
        }
        catch (MsalUiRequiredException)
        {
            try
            {
                // 2. Fallback to interactive login (opens the browser)
                // Using the Embedded Web View keeps the user inside the app flow
                return await _pca.AcquireTokenInteractive(Scopes)
                                 .WithUseEmbeddedWebView(true)
#if ANDROID
                                 .WithParentActivityOrWindow(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity)
#endif
                                 .ExecuteAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MSAL] Interactive Login Failed: {ex.Message}");
                return null;
            }
        }
    }

    public async Task LogoutAsync()
    {
        // Clear all cached accounts from the device
        var accounts = await _pca.GetAccountsAsync();
        while (accounts.Any())
        {
            await _pca.RemoveAsync(accounts.First());
            accounts = await _pca.GetAccountsAsync();
        }
    }
    public async Task<IEnumerable<IAccount>> GetAccounts()
    {
        return await _pca.GetAccountsAsync();
    }
    public async Task<AuthenticationResult> AcquireTokenSilentAsync(IAccount account)
    {
        return await _pca.AcquireTokenSilent(Scopes, account).ExecuteAsync();
    }
}