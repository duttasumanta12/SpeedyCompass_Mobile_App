using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using SpeedyCompass.Backend;
using SpeedyCompass.Backend.Hubs;
using SpeedyCompass.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CompassStateManager>();

// 1. Add SignalR and configure it to use Azure SignalR Service.
// It will automatically look for a connection string in your appsettings.json
// under the key: "Azure:SignalR:ConnectionString"
builder.Services.AddSignalR()
                .AddAzureSignalR();



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

// 2. Map the incoming connections to your Hub
app.MapHub<CompassHub>("/compasshub", config =>
{
    config.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
});

// 3. Simple health check endpoint
app.MapGet("/", () => "Speedy Compass SignalR Server is running!");

//app.MapPost("/api/users/auth/{googleId}", async (string googleId, CompassStateManager state) =>
//{
//    ar account = await state.UserAccounts.Find(u => u.GoogleId == googleId).FirstOrDefaultAsync();
//    if (account == null) return Results.Unauthorized();

//    // DECRYPT before sending back to the owning user
//    return new UserProfileDto
//    {
//        Username = account.Username,
//        EmergencyContact = EncryptionHelper.Decrypt(account.EmergencyContact),
//        VehicleNumber = EncryptionHelper.Decrypt(account.VehicleNumber),
//        BloodGroup = EncryptionHelper.Decrypt(account.BloodGroup),
//        HasConsented = account.HasConsented
//    };

//    return Results.Ok(profile);
//});

// --- NEW: HTTP REST API FOR DASHBOARD ---
// Allows the app to fetch active groups without connecting to SignalR!
app.MapGet("/api/groups", async (CompassStateManager state) =>
{
    var list = new List<ActiveGroupDto>();
    var allGroups = await state.ActiveGroups.Find(_ => true).ToListAsync();

    foreach (var session in allGroups)
    {
        list.Add(new ActiveGroupDto
        {
            GroupName = session.GroupName,
            AdminGoogleId = session.AdminGoogleId,
            MemberCount = state.ConnectedRiders.Values.Count(r => r.GroupName == session.GroupName),
            IsNavigating = session.IsNavigating,
            MaxGroupSize = session.Settings.MaxGroupSize
        });
    }

    return Results.Ok(list);
});

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