namespace SpeedyCompass.Shared.Constants;

[Flags]
public enum NotificationAudience
{
    None = 0,
    Rider = 1,
    Lead = 2,
    Tail = 4,
    Marshal = 8,
    Admin = 16,
    Leadership = Lead | Tail | Marshal | Admin,
    Everyone = Rider | Leadership
}