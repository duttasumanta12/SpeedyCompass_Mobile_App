using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Maps.Handlers;
using SpeedyCompass.Controls;
using SpeedyCompass.Engines;
using SpeedyCompass.Services;
using SpeedyCompass.Shared;
using System.Net.Http;

namespace SpeedyCompass
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiMaps()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("MaterialSymbols.ttf", "MaterialSymbols");
                })
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    handlers.AddHandler<CustomMap, SpeedyCompass.Platforms.Android.CustomMapHandler>();
#endif
                });

            SetupGlobalExceptionHandling();
            builder.Configuration.AddUserSecrets<App>();

            builder.Services.AddSingleton<ILocationBroadcastPolicy, LocationBroadcastPolicy>();
            builder.Services.AddSingleton<AppTierService>();
            builder.Services.AddSingleton<WeatherService>();
            builder.Services.AddSingleton<MsalAuthService>();
            builder.Services.AddSingleton<RideStateService>();
            builder.Services.AddSingleton<SignalRService>();
            builder.Services.AddSingleton<DeviceCapabilityService>();
            builder.Services.AddSingleton<RideSimulatorService>();

            builder.Services.AddSingleton<MapCameraEngine>();
            builder.Services.AddSingleton<IVoiceCopilotEngine, VoiceCopilotEngine>();
            builder.Services.AddSingleton<IRoutingEngine, RoutingEngine>();
            builder.Services.AddSingleton<ITelemetryEngine, TelemetryEngine>();
            builder.Services.AddSingleton<IPlaceDiscoveryService, PlaceDiscoveryService>();
            builder.Services.AddSingleton<RouteDeviationEngine>();

            // Named/Keyed PTT registrations
            builder.Services.AddKeyedSingleton<IPttMeshService, PttMeshService>("pro");
            builder.Services.AddKeyedSingleton<IPttMeshService, NoOpPttMeshService>("free");

            // Default IPttMeshService resolved by tier
            builder.Services.AddSingleton<IPttMeshService>(sp =>
            {
                var tier = sp.GetRequiredService<AppTierService>();
                return tier.UsePttVoice
                    ? sp.GetRequiredKeyedService<IPttMeshService>("pro")
                    : sp.GetRequiredKeyedService<IPttMeshService>("free");
            });

            builder.Services.AddTransient<AuthorizationMessageHandler>();

            builder.Services.AddHttpClient("CompassBackend", client =>
            {
#if DEBUG
                client.BaseAddress = new Uri("https://10.0.2.2:7219/");
#else
                client.BaseAddress = new Uri("https://speedycompassbe-dme4f2hncnb0e4ad.southcentralus-01.azurewebsites.net/");
#endif
                client.Timeout = TimeSpan.FromSeconds(100);
            }).AddHttpMessageHandler<AuthorizationMessageHandler>()
#if DEBUG
.ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
})
#endif
;

            builder.Services.AddSingleton<HardwareButtonService>();
#if ANDROID
            builder.Services.AddSingleton<ILocationTracker, SpeedyCompass.Platforms.Android.AndroidLocationTracker>();
            builder.Services.AddSingleton<IAudioDuckingService, SpeedyCompass.Platforms.Android.AndroidAudioDuckingService>();
            builder.Services.AddSingleton<IRealTimeAudio, AndroidRealTimeAudio>();
#endif

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }

        private static void SetupGlobalExceptionHandling()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
            {
                var ex = (Exception)error.ExceptionObject;
                LogUnhandledException(ex, "AppDomain.UnhandledException");
            };

            TaskScheduler.UnobservedTaskException += (sender, error) =>
            {
                LogUnhandledException(error.Exception, "TaskScheduler.UnobservedTaskException");
                error.SetObserved();
            };
        }

        private static void LogUnhandledException(Exception ex, string source)
        {
            var message = $"\n=======================================================\n" +
                          $"[GLOBAL CRASH HANDLER] Caught via: {source}\n" +
                          $"=======================================================\n" +
                          $"Exception: {ex.Message}\n" +
                          $"Inner Exception: {ex.InnerException?.Message}\n" +
                          $"Stack Trace:\n{ex.StackTrace}\n" +
                          $"=======================================================\n";

            Console.WriteLine(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
