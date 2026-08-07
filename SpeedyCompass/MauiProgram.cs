using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Maps.Handlers;
using SpeedyCompass.Controls;
using SpeedyCompass.Engines;
using SpeedyCompass.Services;
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
                })
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    //handlers.AddHandler<RiderPin, MapPinHandler>();
                    handlers.AddHandler<CustomMap, SpeedyCompass.Platforms.Android.CustomMapHandler>();
#endif
                });

            // 1. Register Global Exception Handlers
            SetupGlobalExceptionHandling();

            builder.Configuration.AddUserSecrets<App>();

            // 1. Register the Services (Singletons live forever)
            builder.Services.AddSingleton<MsalAuthService>();
            builder.Services.AddSingleton<RideStateService>();
            builder.Services.AddSingleton<SignalRService>();

            builder.Services.AddSingleton<IVoiceCopilotEngine, VoiceCopilotEngine>();
            builder.Services.AddSingleton<IRoutingEngine, RoutingEngine>();
            builder.Services.AddSingleton<ITelemetryEngine, TelemetryEngine>();
            builder.Services.AddSingleton<RouteDeviationEngine>();

            // --- NEW: Register HttpClientFactory and your Page ---
            builder.Services.AddHttpClient("CompassBackend", client =>
            {
                client.BaseAddress = new Uri("https://10.0.2.2:7219"); // Centralized URL config
                client.Timeout = TimeSpan.FromSeconds(30);
            }).ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                                (message, cert, chain, errors) => { return true; }
                };
            });

            // Register OS-Specific Location Tracker
            // NEW: Register the Hardware Button bridge
            builder.Services.AddSingleton<HardwareButtonService>();
#if ANDROID
            builder.Services.AddSingleton<ILocationTracker, SpeedyCompass.Platforms.Android.AndroidLocationTracker>();
            
#endif

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
        private static void SetupGlobalExceptionHandling()
        {
            // Catch exceptions that happen on standard application threads
            AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
            {
                var ex = (Exception)error.ExceptionObject;
                LogUnhandledException(ex, "AppDomain.UnhandledException");
            };

            // Catch exceptions that happen inside un-awaited async Tasks
            TaskScheduler.UnobservedTaskException += (sender, error) =>
            {
                LogUnhandledException(error.Exception, "TaskScheduler.UnobservedTaskException");

                // Setting the exception as observed prevents the application from terminating 
                // when the garbage collector cleans up the failed task.
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

            // Output to standard console
            Console.WriteLine(message);

            // Output specifically to Visual Studio / Rider Debug Window
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
