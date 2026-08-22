using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass.Backend.Services.Alerts;

public sealed class EmergencyAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.Emergency;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.Emergency.ToString();
    public string BuildPayload(AlertContext context) => context.SenderName;
}

public sealed class RefuelAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.Refuel;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.Refuel.ToString();
    public string BuildPayload(AlertContext context) => context.SenderName;
}

public sealed class RestAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.Rest;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.Rest.ToString();
    public string BuildPayload(AlertContext context) => context.SenderName;
}

public sealed class MeetupArrivalAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.MeetupArrival;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.MeetupArrival.ToString();
    public string BuildPayload(AlertContext context) => context.SenderName;
}

public sealed class LaggingAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.Lagging;
    public NotificationAudience Audience => NotificationAudience.Leadership;
    public string OutboundAlertType => AlertType.VoicePrompt.ToString();

    public string BuildPayload(AlertContext context)
    {
        var meters = context.DistanceMeters ?? 0;
        string distText = meters > 1000
            ? $"{Math.Round(meters / 1000.0, 1)} kilometers"
            : $"{Math.Round(meters)} meters";

        string statusText = context.IsAhead == true ? "ahead of" : "behind";
        return $"{context.SenderName} is {distText} {statusText} the Lead.";
    }
}

public sealed class SplinterAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.Splinter;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.VoicePrompt.ToString();
    public string BuildPayload(AlertContext context) => "Convoy splintered! The group is stretched too far.";
}

public sealed class PitstopReminderAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.PitstopReminder;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.VoicePrompt.ToString();

    public string BuildPayload(AlertContext context)
    {
        var km = context.DistanceKm ?? 0;
        return $"The Lead has traveled {Math.Round(km)} kilometers. Consider a group rest stop.";
    }
}

public sealed class ArrivalAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.Arrival;
    public NotificationAudience Audience => NotificationAudience.Everyone;
    public string OutboundAlertType => AlertType.VoicePrompt.ToString();
    public string BuildPayload(AlertContext context) => "The Lead is arriving at the destination.";
}

public sealed class RouteDeviationAlertPolicy : IRoleAlertPolicy
{
    public AlertType Type => AlertType.RouteDeviation;
    public NotificationAudience Audience => NotificationAudience.Leadership;
    public string OutboundAlertType => AlertType.VoicePrompt.ToString();

    public string BuildPayload(AlertContext context)
    {
        var rider = string.IsNullOrWhiteSpace(context.SubjectUserName)
            ? context.SenderName
            : context.SubjectUserName;

        return $"Warning. {rider} has deviated from the established route.";
    }
}