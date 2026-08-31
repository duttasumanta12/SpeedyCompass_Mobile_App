using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Web;
using MongoDB.Driver;
using SpeedyCompass.Backend;
using SpeedyCompass.Backend.Hubs;
using SpeedyCompass.Backend.Services.Alerts;
using SpeedyCompass.Backend.Services.External;
using SpeedyCompass.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CompassStateManager>();
builder.Services.AddSingleton<IRoleAlertDispatcher, RoleAlertDispatcher>();
builder.Services.AddSingleton<IRoleAlertPolicy, EmergencyAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, RefuelAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, RestAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, MeetupArrivalAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, LaggingAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, SplinterAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, PitstopReminderAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, ArrivalAlertPolicy>();
builder.Services.AddSingleton<IRoleAlertPolicy, RouteDeviationAlertPolicy>();

builder.Services.AddScoped<RoutingGatewayService>();
builder.Services.AddScoped<WeatherGatewayService>();

// Register MVC controllers
builder.Services.AddControllers();

// 1. Add SignalR and configure it to use Azure SignalR Service.
// It will automatically look for a connection string in your appsettings.json
// under the key: "Azure:SignalR:ConnectionString"
#if DEBUG
builder.Services.AddSignalR().AddHubOptions<CompassHub>(options =>
{
    options.EnableDetailedErrors = true;
});//.AddAzureSignalR();
#else
builder.Services.AddSignalR().AddHubOptions<CompassHub>(options =>
{
    options.EnableDetailedErrors = true;
}).AddAzureSignalR();
#endif

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient("weatherapi", client =>
{
    client.BaseAddress = new Uri("https://api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAdB2C"));

builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context?.Request?.Headers?.Authorization.FirstOrDefault()?.Replace("bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
            var path = context.HttpContext.Request.Path;

            // If the request is for our hub and contains a token, attach it
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/compasshub"))
            {
                // Read the token out of the query string
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// Optional: Add CORS if you plan to test this with a web client later.
// For MAUI mobile apps, CORS isn't strictly necessary, but good practice for mixed platforms.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetIsOriginAllowed(_ => true);
    });
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseRouting();
app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

//// ====================================================================
//// --- NEW: AUTO-MIGRATION EXECUTION ON STARTUP ---
//// ====================================================================
//using (var scope = app.Services.CreateScope())
//{
//    var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService>();
//    // This runs automatically every time the server starts!
//    await migrationService.ApplyMissingMigrationsAsync();
//}

// 2. Map the incoming connections to your Hub
app.MapHub<CompassHub>("/compasshub", config =>
{
    config.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
});

// Map attribute-routed controllers (e.g., GroupsController)
app.MapControllers();

// 3. Simple health check endpoint
app.MapGet("/", () => "Speedy Compass SignalR Server is running!");

// 3. Save User Profile
app.MapPut("/api/users/{googleId}/profile", async (string googleId, UserProfileDto profile, CompassStateManager state) =>
{
    if (string.IsNullOrEmpty(googleId) || string.IsNullOrWhiteSpace(profile.Username))
        return Results.BadRequest("Invalid profile data.");

    var owner = await state.UserAccounts.Find(u => u.Username.ToLower() == profile.Username.ToLower()).FirstOrDefaultAsync();
    if (owner != null && owner.GoogleId != googleId)
    {
        return Results.Conflict($"The username '{profile.Username}' is already taken.");
    }

    // ENCRYPT the PII fields before they touch the database
    var update = Builders<UserAccount>.Update
        .Set(u => u.Username, profile.Username.Trim())
        .Set(u => u.EmergencyContact, EncryptionHelper.Encrypt(profile.EmergencyContact?.Trim() ?? ""))
        .Set(u => u.VehicleNumber, EncryptionHelper.Encrypt(profile.VehicleNumber?.Trim() ?? ""))
        .Set(u => u.BloodGroup, EncryptionHelper.Encrypt(profile.BloodGroup ?? ""))
        .Set(u => u.HasConsented, profile.HasConsented);

    await state.UserAccounts.UpdateOneAsync(
        u => u.GoogleId == googleId,
        update,
        new UpdateOptions { IsUpsert = true }
    );

    return Results.Ok(true);
});

app.Run();