using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass.Backend.Services.Alerts;

public sealed record AlertContext(
    string GroupName,
    string SenderName,
    string? SubjectUserName = null,
    double? DistanceKm = null,
    double? DistanceMeters = null,
    bool? IsAhead = null);

public interface IRoleAlertPolicy
{
    AlertType Type { get; }
    NotificationAudience Audience { get; }
    string OutboundAlertType { get; }
    string BuildPayload(AlertContext context);
}