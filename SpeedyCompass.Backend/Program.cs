using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SpeedyCompass.Backend.Hubs;
using Microsoft.Azure.SignalR;

var builder = WebApplication.CreateBuilder(args);

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

app.Run();