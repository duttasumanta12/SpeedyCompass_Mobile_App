namespace SpeedyCompass.Shared.Constants
{
    public static class TelemetryStatus
    {
        public const string Ahead = "Ahead";
        public const string Behind = "Behind";
        public const string LeadRider = "Lead Rider";
        public const string AwaitingGps = "Awaiting GPS";
        public const string InFormation = "In Formation";
        public const string Separated = "Separated";
        public const string NearLead = "Near Lead";
    }
    public enum GroupState
    {
        NotNavigating,    // Hanging out in the lobby, no destination set
        DestinationSet,    // Destination set, but not yet started
        Navigating,       // Actively riding towards a destination
        PausedBreak,      // Deliberate stop for food/fuel/rest
        PausedHazard,     // Forced stop due to weather, traffic, or accident
        PausedMechanical, // Forced stop due to bike issues
        Completed         // Successfully arrived at the destination
    }
    public static class PreferencesConstants
    {
        // local map visual standard toggles (Phase 1/2 reuse)
        public const string Map_Traffic = "Map_Traffic";
        public const string Map_Style = "Map_Style";
        public const string Map_HeadingUp = "Map_HeadingUp";
        public const string Map_VoiceNav = "Map_VoiceNav";

        // standard Local optimizations (The new battery standard stuff) standard
        public const string Map_NavigationMode = "Map_NavigationMode"; // Perspective Choice standard
        public const string Map_GPSUpdateAggressiveness = "Map_GPSUpdateAggressiveness";
        public const string Map_BackgroundBatteryThrottlePercentage = "Map_BackgroundBatteryThrottlePercentage";
    }
}
