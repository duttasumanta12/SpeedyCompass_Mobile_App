namespace SpeedyCompass.Shared;

public static class RiderNameHelper
{
    public static string ToDisplayName(string rawName, bool isYou, bool isOnline, bool isHidden)
    {
        var name = rawName;
        if (isYou) name += " (You)";
        if (!isOnline) name += " (Offline)";
        if (isHidden) name += " (Hidden)";
        return name;
    }

    public static string ToRawName(string displayName) =>
        displayName
            .Replace(" (You)", string.Empty, StringComparison.Ordinal)
            .Replace(" (Offline)", string.Empty, StringComparison.Ordinal)
            .Replace(" (Hidden)", string.Empty, StringComparison.Ordinal)
            .Replace(" 👻 (Hidden)", string.Empty, StringComparison.Ordinal)
            .Trim();
}