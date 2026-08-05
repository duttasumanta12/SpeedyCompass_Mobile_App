using System.Diagnostics;

namespace SpeedyCompass.Shared;

public static class AppLogger
{
    // Generates a unique ID for the current ride session
    public static string CurrentRideCorrelationId { get; private set; } = Guid.NewGuid().ToString();

    public static void ResetRideCorrelationId()
    {
        CurrentRideCorrelationId = Guid.NewGuid().ToString();
        Info("AppLogger", $"New Ride Session Started. Correlation ID: {CurrentRideCorrelationId}");
    }

    public static void Info(string module, string message)
    {
#if DEBUG
        Debug.WriteLine($"[INFO] [{DateTime.Now:HH:mm:ss}] [{CurrentRideCorrelationId}] [{module}] {message}");
#endif
    }

    public static void Error(string module, Exception ex, string customMessage = "")
    {
        string baseMsg = string.IsNullOrEmpty(customMessage) ? ex.Message : customMessage;

#if DEBUG
        // Full stack trace for you in Debug
        Debug.WriteLine($"\n[ERROR] [{DateTime.Now:HH:mm:ss}] [{CurrentRideCorrelationId}] [{module}] {baseMsg}");
        Debug.WriteLine($"---> {ex.StackTrace}\n");
#else
        // Silent/Safe logging in Release (Hook this up to AppCenter/Crashlytics later!)
        Console.WriteLine($"[ERROR] [{module}] {baseMsg}"); 
#endif
    }
}