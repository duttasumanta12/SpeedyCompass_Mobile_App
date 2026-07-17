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
}
