namespace SpeedyCompass.Shared.Constants
{
    public static class TelemetryStatus
    {
        public const string Ahead = "Ahead";
        public const string Behind = "Behind";
        public const string Away = "Away";
        public const string Nearby = "Nearby";
        public const string LeadRider = "Lead Rider";
        public const string AwaitingGps = "Awaiting GPS";
        public const string InFormation = "In Formation";
        public const string Separated = "Separated";
        public const string NearLead = "Near Lead";
    }

    public enum RiderRole
    {
        Rider = 0,
        Lead = 1,
        Tail = 2,
        Marshal = 3,
        Admin = 4
    }

    public enum RiderGapStatus
    {
        Nearby = 0,
        Ahead = 1,
        Behind = 2,
        Away = 3
    }

    public static class RiderRoleParser
    {
        public static RiderRole ParseOrDefault(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return RiderRole.Rider;

            return Enum.TryParse<RiderRole>(value, ignoreCase: true, out var parsed)
                ? parsed
                : RiderRole.Rider;
        }
    }

    public enum GroupState
    {
        NotNavigating,
        DestinationSet,
        Navigating,
        PausedBreak,
        PausedHazard,
        PausedMechanical,
        Completed
    }

    public static class PreferencesConstants
    {
        public const string Map_Traffic = "Map_Traffic";
        public const string Map_Style = "Map_Style";
        public const string Map_HeadingUp = "Map_HeadingUp";
        public const string Map_VoiceNav = "Map_VoiceNav";
        public const string Map_NavigationMode = "Map_NavigationMode";
        public const string Map_GPSUpdateAggressiveness = "Map_GPSUpdateAggressiveness";
        public const string Map_BackgroundBatteryThrottlePercentage = "Map_BackgroundBatteryThrottlePercentage";
    }

    public enum TurnDirectionEnum
    {
        Straight,
        TurnLeft,
        TurnRight,
        SharpLeft,
        SharpRight,
        SlightLeft,
        SlightRight,
        KeepLeft,
        KeepRight,
        UTurnLeft,
        UTurnRight,
        RoundaboutLeft,
        RoundaboutRight,
        RampLeft,
        RampRight,
        ForkLeft,
        ForkRight,
        Merge,
        Destination
    }
}
