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
builder.Services.AddSignalR().AddHubOptions<CompassHub>(options =>
{
    options.EnableDetailedErrors = true;
})
                .AddAzureSignalR();

builder.Services.AddMemoryCache();



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

// --- UPDATED: HTTP REST API FOR DASHBOARD ---
// Allows the app to fetch active groups and dynamically check membership!
app.MapGet("/api/groups", async (string googleId, CompassStateManager state) =>
{
    var groups = await state.ActiveGroups.Find(_ => true).ToListAsync();

    var groupList = new List<object>();
    foreach (var g in groups)
    {
        // 1. Count total members associated with this group in the new table
        long memberCount = await state.GroupMembers.CountDocumentsAsync(m => m.GroupName == g.GroupName);

        // 2. Check if the specific user requesting the list is already in the group
        bool isMember = !string.IsNullOrEmpty(googleId) &&
                        await state.GroupMembers.Find(m => m.GroupName == g.GroupName && m.GoogleId == googleId).AnyAsync();

        groupList.Add(new
        {
            GroupName = g.GroupName,
            MemberCount = (int)memberCount,
            MaxGroupSize = g.Settings.MaxGroupSize,
            AdminGoogleId = g.AdminGoogleId,
            IsMember = isMember
        });
    }

    return Results.Ok(groupList);
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