namespace SpeedyCompass.Models
{
    /// <summary>
    /// Represents the telemetry and analytics for a completed group ride.
    /// </summary>
    public class RideSummary
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public DateTime RideDate { get; set; } = DateTime.Now;

        public string GroupName { get; set; }

        public string DestinationName { get; set; }

        // Core Metrics
        public double TotalDistanceKm { get; set; }

        public double TopSpeedKmh { get; set; }

        public double AverageMovingSpeedKmh { get; set; }

        // Time Analytics
        public TimeSpan TotalElapsedTime { get; set; }

        public TimeSpan MovingTime { get; set; }

        public TimeSpan StoppedTime { get; set; }
    }
}
