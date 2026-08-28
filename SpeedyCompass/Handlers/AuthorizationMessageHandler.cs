using System.Net.Http.Headers;
using Microsoft.Identity.Client;

namespace SpeedyCompass.Services;

public class AuthorizationMessageHandler : DelegatingHandler
{
    private readonly MsalAuthService _authService;

    public AuthorizationMessageHandler(MsalAuthService authService)
    {
        _authService = authService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1. Get the current user account
        var accounts = await _authService.GetAccounts();
        var account = accounts.FirstOrDefault();

        if (account != null)
        {
            try
            {
                // 2. Attempt to get a valid token silently (MSAL handles caching and refresh tokens)
                var authResult = await _authService.AcquireTokenSilentAsync(account);

                // 3. Attach the token to the request header
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult.AccessToken);
            }
            catch (MsalUiRequiredException)
            {
                // The token expired and the refresh token is invalid.
                // We let the request continue without a token. 
                // The API will return 401 Unauthorized, and your UI should react by prompting the user to log in again.
                System.Diagnostics.Debug.WriteLine("[Auth] Silent token acquisition failed. UI login required.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] Error attaching token: {ex.Message}");
            }
        }

        // 4. Proceed with the HTTP request
        return await base.SendAsync(request, cancellationToken);
    }
}