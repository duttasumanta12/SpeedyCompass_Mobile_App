using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.Graphics;

namespace SpeedyCompass.Engines
{
    /// <summary>
    /// Google Maps-style rerouting engine
    /// Multi-factor deviation detection with adaptive thresholds
    /// </summary>
    public class RouteDeviationEngine
    {
        public enum DeviationSeverity
        {
            None = 0,           // On route
            Minor = 1,          // Slight drift but acceptable  
            Moderate = 2,       // Off route but recoverable
            Severe = 3,         // Definitely lost, reroute needed
        }

        public class RouteDeviationAnalysis
        {
            public bool IsOffRoute { get; set; }
            public DeviationSeverity Severity { get; set; }
            public double ConfidenceScore { get; set; } // 0.0 - 1.0
            public string Reason { get; set; }
            public int StrikesAccumulated { get; set; }
            public string UserMessage { get; set; }
            public Color AlertColor { get; set; }
            public double DistanceToRouteMeters { get; set; }
            public double HeadingDifferenceDegreesFromRoute { get; set; }
            public int ClosestRouteIndex { get; set; }
        }

        /// <summary>
        /// Analyze multi-factor route deviation using Google Maps-style logic
        /// </summary>
        public RouteDeviationAnalysis AnalyzeRouteDeviation(
            Location currentLocation,
            List<Location> routePoints,
            int currentRouteIndex,
            double currentHeading,
            double currentSpeedKmh,
            int offRouteStrikeCount)
        {
            var analysis = new RouteDeviationAnalysis
            {
                IsOffRoute = false,
                Severity = DeviationSeverity.None,
                ConfidenceScore = 0.0,
                StrikesAccumulated = offRouteStrikeCount,
                AlertColor = Colors.DodgerBlue
            };

            // ============================================================
            // FACTOR 0: SKIP IF STATIONARY
            // ============================================================
            if (currentSpeedKmh < 2)
            {
                analysis.Reason = "Stationary - not judging position";
                return analysis;
            }

            // ============================================================
            // FACTOR 1: FIND CLOSEST POINT ON ROUTE
            // ============================================================
            var (closestIndex, distanceToRoute) = FindClosestPointOnRoute(
                currentLocation,
                routePoints,
                currentRouteIndex);

            analysis.ClosestRouteIndex = closestIndex;
            double distanceToRouteMeters = distanceToRoute * 1000;
            analysis.DistanceToRouteMeters = distanceToRouteMeters;

            // ============================================================
            // FACTOR 2: SPEED-DEPENDENT DISTANCE THRESHOLD
            // ============================================================
            double speedBasedThreshold = CalculateSpeedBasedThreshold(currentSpeedKmh);

            // CRITICAL: Way too far from any part of the route
            if (distanceToRouteMeters > speedBasedThreshold * 1.5)
            {
                analysis.Severity = DeviationSeverity.Severe;
                analysis.ConfidenceScore = Math.Min(1.0, distanceToRouteMeters / (speedBasedThreshold * 2.0));
                analysis.Reason = $"Too far from route: {distanceToRouteMeters:F0}m (threshold: {speedBasedThreshold:F0}m)";
                analysis.IsOffRoute = true;
                analysis.UserMessage = $"Off route - {distanceToRouteMeters:F0}m away";
                analysis.AlertColor = Colors.Red;
                return analysis;
            }

            // ============================================================
            // FACTOR 3: HEADING ALIGNMENT CHECK
            // ============================================================
            double expectedHeading = 0;
            double headingDifference = 0;

            if (closestIndex < routePoints.Count - 1)
            {
                // Look ahead 5 points for smoother route heading calculation
                var lookAheadIndex = Math.Min(closestIndex + 5, routePoints.Count - 1);
                expectedHeading = CalculateBearing(routePoints[closestIndex], routePoints[lookAheadIndex]);
                headingDifference = NormalizeHeadingDifference(currentHeading, expectedHeading);

                analysis.HeadingDifferenceDegreesFromRoute = headingDifference;

                // Speed-dependent heading tolerance
                double headingTolerance = currentSpeedKmh > 60 ? 60.0 : 30.0;

                if (headingDifference > headingTolerance)
                {
                    double headingConfidence = Math.Min(1.0, headingDifference / 180.0);

                    // Only count as OFF if ALSO at distance threshold
                    if (distanceToRouteMeters > speedBasedThreshold)
                    {
                        analysis.Severity = DeviationSeverity.Severe;
                        analysis.ConfidenceScore = Math.Max(headingConfidence, analysis.ConfidenceScore);
                        analysis.Reason = $"Wrong direction: {headingDifference:F0}° off + {distanceToRouteMeters:F0}m away";
                        analysis.IsOffRoute = true;
                        analysis.UserMessage = $"Wrong direction - {headingDifference:F0}° off";
                        analysis.AlertColor = Colors.Red;
                        return analysis;
                    }
                    else if (distanceToRouteMeters > speedBasedThreshold * 0.6)
                    {
                        // Minor distance + wrong heading = Moderate concern
                        analysis.Severity = DeviationSeverity.Moderate;
                        analysis.ConfidenceScore = (headingConfidence + distanceToRouteMeters / speedBasedThreshold) / 2.0;
                        analysis.Reason = $"Possible wrong turn: {headingDifference:F0}° + {distanceToRouteMeters:F0}m";
                        analysis.UserMessage = $"Possible wrong turn";
                        analysis.AlertColor = Colors.OrangeRed;
                        return analysis;
                    }
                }
            }

            // ============================================================
            // FACTOR 4: ROAD GEOMETRY ANALYSIS
            // ============================================================
            var (onAlternateRoad, alternateRoadConfidence) = CheckAlternateRoadPossibility(
                currentLocation,
                routePoints,
                closestIndex,
                currentHeading);

            if (onAlternateRoad && alternateRoadConfidence > 0.7)
            {
                // They might be on a valid parallel route (e.g., service road)
                analysis.Severity = DeviationSeverity.Minor;
                analysis.Reason = "On possible alternate road (same direction)";
                analysis.ConfidenceScore = alternateRoadConfidence;
                analysis.UserMessage = "On alternate route";
                analysis.AlertColor = Colors.Orange;
                return analysis;
            }

            // ============================================================
            // FACTOR 5: HISTORICAL STABILITY GRACE PERIOD
            // ============================================================
            if (offRouteStrikeCount == 0 && distanceToRouteMeters < speedBasedThreshold * 0.8)
            {
                analysis.Severity = DeviationSeverity.None;
                analysis.Reason = "Within acceptable bounds";
                analysis.ConfidenceScore = 1.0 - (distanceToRouteMeters / speedBasedThreshold);
                return analysis;
            }

            // ============================================================
            // DEFAULT: ON ROUTE (but drift detected)
            // ============================================================
            if (distanceToRouteMeters > speedBasedThreshold * 0.5)
            {
                analysis.Severity = DeviationSeverity.Minor;
                analysis.Reason = $"Minor drift: {distanceToRouteMeters:F0}m from route";
                analysis.ConfidenceScore = 0.5;
                analysis.UserMessage = "Slight drift from route";
                analysis.AlertColor = Colors.Orange;
            }
            else
            {
                analysis.Severity = DeviationSeverity.None;
                analysis.Reason = "On route";
                analysis.ConfidenceScore = 1.0 - (distanceToRouteMeters / speedBasedThreshold);
            }

            return analysis;
        }

        /// <summary>
        /// Speed-based dynamic threshold (like Google Maps)
        /// Accounts for GPS noise at different speeds
        /// </summary>
        public double CalculateSpeedBasedThreshold(double speedKmh)
        {
            return speedKmh switch
            {
                < 10 => 30,      // Walking/parking: strict 30m
                < 30 => 50,      // City streets: 50m
                < 60 => 80,      // Suburban/arterial: 80m
                < 100 => 120,    // Highway start: 120m
                _ => 150         // Full highway: 150m
            };
        }

        /// <summary>
        /// Normalize heading difference to 0-180 range
        /// </summary>
        public double NormalizeHeadingDifference(double heading1, double heading2)
        {
            double diff = Math.Abs(heading1 - heading2);
            return diff > 180 ? 360 - diff : diff;
        }

        /// <summary>
        /// Calculate bearing between two locations (in degrees)
        /// </summary>
        public double CalculateBearing(Location start, Location end)
        {
            double lat1 = start.Latitude * (Math.PI / 180.0);
            double lon1 = start.Longitude * (Math.PI / 180.0);
            double lat2 = end.Latitude * (Math.PI / 180.0);
            double lon2 = end.Longitude * (Math.PI / 180.0);

            double dLon = lon2 - lon1;

            double y = Math.Sin(dLon) * Math.Cos(lat2);
            double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);

            double bearing = Math.Atan2(y, x) * (180.0 / Math.PI);
            return (bearing + 360.0) % 360.0;
        }

        /// <summary>
        /// Find closest point on route with optimized search radius
        /// </summary>
        public (int Index, double Distance) FindClosestPointOnRoute(
            Location current,
            List<Location> routePoints,
            int startIndex)
        {
            if (routePoints.Count == 0) return (0, double.MaxValue);

            // Search window: current position ± adaptive range
            int searchStart = Math.Max(0, startIndex - 10);
            int searchEnd = Math.Min(routePoints.Count - 1, startIndex + 30);

            double minDistance = double.MaxValue;
            int closestIndex = startIndex;

            for (int i = searchStart; i <= searchEnd; i++)
            {
                double dist = Location.CalculateDistance(current, routePoints[i], DistanceUnits.Kilometers);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    closestIndex = i;
                }
            }

            return (closestIndex, minDistance);
        }

        /// <summary>
        /// Check if rider might be on a parallel/alternate road
        /// (e.g., service road parallel to highway, different lane on divided highway)
        /// </summary>
        public (bool OnAlternate, double Confidence) CheckAlternateRoadPossibility(
            Location current,
            List<Location> routePoints,
            int closestIndex,
            double currentHeading)
        {
            if (closestIndex < 5 || closestIndex > routePoints.Count - 5)
                return (false, 0);

            // Look at the route's heading in this segment (smooth approximation)
            var prevPoint = routePoints[Math.Max(0, closestIndex - 3)];
            var nextPoint = routePoints[Math.Min(routePoints.Count - 1, closestIndex + 3)];

            double routeHeading = CalculateBearing(prevPoint, nextPoint);
            double headingDiff = NormalizeHeadingDifference(currentHeading, routeHeading);

            // If rider heading closely matches route heading despite being 80-150m away,
            // they're likely on a parallel road going the same direction
            if (headingDiff < 25) // Very close heading alignment
            {
                return (true, 0.85);
            }

            if (headingDiff < 35) // Close enough
            {
                return (true, 0.65);
            }

            return (false, 0);
        }

        /// <summary>
        /// Update strike system based on deviation analysis
        /// Weighted by severity for realistic reroute triggering
        /// </summary>
        public int UpdateDeviationStrikes(RouteDeviationAnalysis analysis, int currentStrikes)
        {
            switch (analysis.Severity)
            {
                case DeviationSeverity.None:
                    // Forgive instantly if back on track
                    return 0;

                case DeviationSeverity.Minor:
                    // Minor drifts: only add strike if high confidence
                    if (analysis.ConfidenceScore > 0.7)
                    {
                        return currentStrikes + 1;
                    }
                    return Math.Max(0, currentStrikes - 1); // Slowly forgive

                case DeviationSeverity.Moderate:
                    // Moderate issues add 2 strikes
                    return currentStrikes + 2;

                case DeviationSeverity.Severe:
                    // Severe issues: immediate action (add 3 strikes)
                    return currentStrikes + 3;

                default:
                    return currentStrikes;
            }
        }

        /// <summary>
        /// Adaptive reroute threshold based on severity
        /// Severe deviations need fewer strikes to trigger reroute
        /// </summary>
        public int GetRerouteStrikeThreshold(RouteDeviationAnalysis analysis)
        {
            return analysis.Severity switch
            {
                DeviationSeverity.Severe => 1,      // Immediate reroute
                DeviationSeverity.Moderate => 2,    // Quick reroute (1-2 bad pings)
                DeviationSeverity.Minor => 4,       // Lenient reroute (multiple drifts)
                _ => 999                             // Never reroute if on-route
            };
        }

        /// <summary>
        /// Smart reroute throttling like Google Maps
        /// Prevents spam while allowing rapid response when needed
        /// </summary>
        public bool ShouldPerformReroute(
            RouteDeviationAnalysis analysis,
            bool officiallyLost,
            DateTime lastRerouteTime)
        {
            if (!officiallyLost)
                return false;

            double timeSinceLastReroute = (DateTime.Now - lastRerouteTime).TotalSeconds;

            // ADAPTIVE THROTTLING based on severity
            double minSecondsBetweenReroutes = analysis.Severity switch
            {
                DeviationSeverity.Severe => 5,      // Severe: quick response
                DeviationSeverity.Moderate => 10,   // Moderate: responsive
                DeviationSeverity.Minor => 20,      // Minor: patient
                _ => 60                              // Paranoid safety
            };

            // Check if enough time has passed
            bool timeOkay = timeSinceLastReroute > minSecondsBetweenReroutes;

            // Confidence must be high before we act
            bool confidenceOkay = analysis.ConfidenceScore > 0.75;

            return timeOkay && confidenceOkay;
        }
    }
}
