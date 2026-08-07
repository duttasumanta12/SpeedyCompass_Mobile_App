# Google Maps-Style Rerouting Logic Implementation Guide

## Current Issues in Your Code

### 1. **Simple Distance-Only Detection** ❌
```csharp
// Current: Too simplistic
if (minDistance > 0.15) currentPingIsOffRoute = true;
```
**Problem:** Doesn't account for:
- Which direction rider is heading
- Parallel roads (highways with service roads)
- Actual navigation intent

### 2. **Single Heading Check** ❌
```csharp
double headingDiff = Math.Abs(currentLocation.Course.Value - expectedHeading);
if (headingDiff > 60) currentPingIsOffRoute = true;
```
**Problem:**
- 60° threshold is arbitrary
- Doesn't consider road geometry
- No weighting for distance + heading combo

### 3. **No Context Awareness** ❌
- Doesn't check if rider took an alternate valid road
- No consideration of confidence levels
- No learning from recent history

### 4. **15-Second Debounce is Harsh** ❌
```csharp
if ((DateTime.Now - _rideCache.LastRerouteTime).TotalSeconds > 15)
```
**Problem:** Forces 15-second wait even for obvious deviations, bad UX

---

## 🎯 Google Maps-Style Solution

### Phase 1: Multi-Factor Deviation Detection

```csharp
/// <summary>
/// Multi-factor off-route detection like Google Maps
/// Combines: Distance, Heading, Speed, and Road Geometry
/// </summary>
private class RouteDeviationAnalysis
{
    public bool IsOffRoute { get; set; }
    public DeviationSeverity Severity { get; set; } // None, Minor, Moderate, Severe
    public double ConfidenceScore { get; set; } // 0.0 - 1.0
    public string Reason { get; set; }
    public int StrikesAccumulated { get; set; }
    
    // For UI feedback
    public string UserMessage { get; set; }
    public Color AlertColor { get; set; }
}

public enum DeviationSeverity
{
    None = 0,           // On route
    Minor = 1,          // Slight drift but acceptable
    Moderate = 2,       // Off route but recoverable
    Severe = 3,         // Definitely lost, reroute needed
}

/// <summary>
/// Google Maps-style rerouting detection using multi-factor analysis
/// </summary>
private RouteDeviationAnalysis AnalyzeRouteDeviation(
    Location currentLocation,
    List<Location> routePoints,
    int currentRouteIndex,
    double currentHeading,
    double currentSpeedKmh)
{
    var analysis = new RouteDeviationAnalysis
    {
        IsOffRoute = false,
        Severity = DeviationSeverity.None,
        ConfidenceScore = 0.0,
        StrikesAccumulated = _rideCache.OffRouteStrikeCount
    };

    // ============================================================
    // SKIP IF NOT MOVING
    // ============================================================
    if (currentSpeedKmh < 2) // Stationary or moving very slowly
    {
        analysis.Reason = "Stationary";
        return analysis;
    }

    // ============================================================
    // FACTOR 1: FIND CLOSEST POINT ON ROUTE (with optimized search)
    // ============================================================
    var (closestIndex, distanceToRoute) = FindClosestPointOnRoute(
        currentLocation, 
        routePoints, 
        currentRouteIndex);

    // Convert to meters for easier reading
    double distanceToRouteMeters = distanceToRoute * 1000;

    // ============================================================
    // FACTOR 2: SPEED-DEPENDENT DISTANCE THRESHOLD
    // ============================================================
    // At low speed: stricter tolerance (50m)
    // At highway speed (100+ km/h): more lenient (150m)
    // Reason: GPS noise is more forgivable at high speed
    double speedBasedThreshold = CalculateSpeedBasedThreshold(currentSpeedKmh);

    if (distanceToRouteMeters > speedBasedThreshold * 1.5) // Critical threshold
    {
        analysis.Severity = DeviationSeverity.Severe;
        analysis.ConfidenceScore = Math.Min(1.0, distanceToRouteMeters / (speedBasedThreshold * 2.0));
        analysis.Reason = $"Too far from route: {distanceToRouteMeters:F0}m (threshold: {speedBasedThreshold}m)";
        analysis.IsOffRoute = true;
        return analysis;
    }

    // ============================================================
    // FACTOR 3: HEADING ALIGNMENT CHECK
    // ============================================================
    if (closestIndex < routePoints.Count - 1)
    {
        double expectedHeading = CalculateBearing(
            routePoints[closestIndex], 
            routePoints[Math.Min(closestIndex + 5, routePoints.Count - 1)]); // Look ahead 5 points
        
        double headingDifference = NormalizeHeadingDifference(currentHeading, expectedHeading);

        // Speed-dependent heading tolerance
        // At slow speed: ±30° (strict, easy to see if wrong turn)
        // At highway speed: ±60° (forgiving for lane changes)
        double headingTolerance = currentSpeedKmh > 60 ? 60.0 : 30.0;

        if (headingDifference > headingTolerance)
        {
            // Heading is way off - this is a red flag
            double headingConfidence = Math.Min(1.0, headingDifference / 180.0);
            
            // Only count as OFF if ALSO at distance threshold
            if (distanceToRouteMeters > speedBasedThreshold)
            {
                analysis.Severity = DeviationSeverity.Severe;
                analysis.ConfidenceScore = Math.Max(headingConfidence, analysis.ConfidenceScore);
                analysis.Reason = $"Wrong direction: {headingDifference:F0}° off + {distanceToRouteMeters:F0}m away";
                analysis.IsOffRoute = true;
                return analysis;
            }
            else if (distanceToRouteMeters > speedBasedThreshold * 0.6)
            {
                // Minor distance + wrong heading = Moderate concern
                analysis.Severity = DeviationSeverity.Moderate;
                analysis.ConfidenceScore = (headingConfidence + distanceToRouteMeters / speedBasedThreshold) / 2.0;
                analysis.Reason = $"Possible wrong turn: {headingDifference:F0}° + {distanceToRouteMeters:F0}m";
                analysis.IsOffRoute = false; // Not yet confirmed
                return analysis;
            }
        }
    }

    // ============================================================
    // FACTOR 4: ROAD GEOMETRY ANALYSIS
    // ============================================================
    // Check if rider might be on a parallel/alternate road
    var (onAlternateRoad, alternateRoadConfidence) = CheckAlternateRoadPossibility(
        currentLocation,
        routePoints,
        closestIndex,
        currentHeading);

    if (onAlternateRoad && alternateRoadConfidence > 0.7)
    {
        // They might be on a valid parallel route (e.g., service road)
        analysis.Severity = DeviationSeverity.Minor;
        analysis.Reason = "On possible alternate road";
        analysis.ConfidenceScore = alternateRoadConfidence;
        return analysis;
    }

    // ============================================================
    // FACTOR 5: HISTORICAL STABILITY
    // ============================================================
    // If rider was just confirmed on-route, give them grace period
    if (_rideCache.OffRouteStrikeCount == 0 && distanceToRouteMeters < speedBasedThreshold * 0.8)
    {
        analysis.Severity = DeviationSeverity.None;
        analysis.Reason = "Within acceptable bounds";
        return analysis;
    }

    // ============================================================
    // DEFAULT: ON ROUTE
    // ============================================================
    analysis.Severity = DeviationSeverity.None;
    analysis.ConfidenceScore = 1.0 - (distanceToRouteMeters / speedBasedThreshold);
    return analysis;
}

/// <summary>
/// Speed-based dynamic threshold (like Google Maps)
/// </summary>
private double CalculateSpeedBasedThreshold(double speedKmh)
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
private double NormalizeHeadingDifference(double heading1, double heading2)
{
    double diff = Math.Abs(heading1 - heading2);
    return diff > 180 ? 360 - diff : diff;
}

/// <summary>
/// Find closest point on route with optimized search radius
/// </summary>
private (int Index, double Distance) FindClosestPointOnRoute(
    Location current,
    List<Location> routePoints,
    int startIndex)
{
    if (routePoints.Count == 0) return (0, double.MaxValue);

    // Search window: current position ± 10 points ahead (adaptive)
    int searchStart = Math.Max(0, startIndex - 5);
    int searchEnd = Math.Min(routePoints.Count - 1, startIndex + 20);

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
/// (e.g., service road parallel to highway)
/// </summary>
private (bool OnAlternate, double Confidence) CheckAlternateRoadPossibility(
    Location current,
    List<Location> routePoints,
    int closestIndex,
    double currentHeading)
{
    if (closestIndex < 5 || closestIndex > routePoints.Count - 5) 
        return (false, 0);

    // Look at the route's heading in this segment
    var prevPoint = routePoints[Math.Max(0, closestIndex - 3)];
    var nextPoint = routePoints[Math.Min(routePoints.Count - 1, closestIndex + 3)];
    
    double routeHeading = CalculateBearing(prevPoint, nextPoint);
    double headingDiff = NormalizeHeadingDifference(currentHeading, routeHeading);

    // If rider heading closely matches route heading despite being 100m away,
    // they're likely on a parallel road going the same direction
    if (headingDiff < 30)
    {
        return (true, 0.8);
    }

    return (false, 0);
}
```

---

### Phase 2: Smart Strike System

```csharp
/// <summary>
/// Advanced strike system with adaptive thresholds
/// Google Maps uses ~3 strikes, but we're smarter about it
/// </summary>
private void UpdateDeviationStrikes(RouteDeviationAnalysis analysis)
{
    switch (analysis.Severity)
    {
        case DeviationSeverity.None:
            // Forgive instantly if back on track
            if (_rideCache.OffRouteStrikeCount > 0)
            {
                AppLogger.Info("Routing", "Back on route - strikes cleared");
                _rideCache.OffRouteStrikeCount = 0;
            }
            break;

        case DeviationSeverity.Minor:
            // Minor drifts don't add strikes yet (confidence < 0.7)
            if (analysis.ConfidenceScore > 0.7)
            {
                _rideCache.OffRouteStrikeCount++;
                AppLogger.Info("Routing", $"Minor deviation - Strike {_rideCache.OffRouteStrikeCount}");
            }
            break;

        case DeviationSeverity.Moderate:
            // Moderate issues add 2 strikes (more serious)
            _rideCache.OffRouteStrikeCount += 2;
            AppLogger.Info("Routing", $"Moderate deviation - Strike count: {_rideCache.OffRouteStrikeCount}");
            break;

        case DeviationSeverity.Severe:
            // Severe issues add 3 strikes immediately
            _rideCache.OffRouteStrikeCount += 3;
            AppLogger.Info("Routing", $"Severe deviation - Strike count: {_rideCache.OffRouteStrikeCount}");
            break;
    }
}

/// <summary>
/// Adaptive reroute threshold based on severity
/// </summary>
private int GetRerouteStrikeThreshold(RouteDeviationAnalysis analysis)
{
    return analysis.Severity switch
    {
        DeviationSeverity.Severe => 1,      // Immediate reroute
        DeviationSeverity.Moderate => 2,    // Quick reroute
        DeviationSeverity.Minor => 4,       // Lenient reroute
        _ => 999                             // Never reroute
    };
}
```

---

### Phase 3: Adaptive Reroute Throttling

```csharp
/// <summary>
/// Smart reroute throttling like Google Maps
/// Prevents spam while allowing rapid response when needed
/// </summary>
private bool ShouldPerformReroute(RouteDeviationAnalysis analysis, bool officiallyLost)
{
    if (!officiallyLost)
        return false;

    double timeSinceLastReroute = (DateTime.Now - _rideCache.LastRerouteTime).TotalSeconds;

    // ADAPTIVE THROTTLING based on severity
    double minSecondsBetweenReroutes = analysis.Severity switch
    {
        DeviationSeverity.Severe => 5,      // Severe: reroute every 5 seconds (fast response)
        DeviationSeverity.Moderate => 10,   // Moderate: every 10 seconds
        DeviationSeverity.Minor => 20,      // Minor: every 20 seconds
        _ => 30                              // Paranoid safety: 30 seconds minimum
    };

    // Check if enough time has passed
    bool timeOkay = timeSinceLastReroute > minSecondsBetweenReroutes;

    // Additional check: confidence must be high before we act
    bool confidenceOkay = analysis.ConfidenceScore > 0.75;

    if (timeOkay && confidenceOkay)
    {
        AppLogger.Info("Routing", 
            $"Reroute approved: {timeSinceLastReroute:F0}s since last, confidence {analysis.ConfidenceScore:P0}");
        _rideCache.LastRerouteTime = DateTime.Now;
        return true;
    }

    return false;
}
```

---

### Phase 4: Integration into TrimRouteVisuals

```csharp
private async Task TrimRouteVisuals(Location currentLocation)
{
    if (_activeRouteLine == null || _rideCache.ActiveDestination == null || 
        _rideCache.CurrentRoutePoints.Count < 2) 
        return;
    if (_rideCts == null || _rideCts.IsCancellationRequested) return;

    var currentRouteSnapshot = _rideCache.CurrentRoutePoints.ToList();

    try
    {
        var telemetryData = await Task.Run(() =>
        {
            _rideCts.Token.ThrowIfCancellationRequested();

            double currentSpeedKmh = (currentLocation?.Speed ?? 0) * 3.6;
            double currentHeading = currentLocation?.Course ?? 0;

            // ============================================================
            // THE NEW MAGIC: MULTI-FACTOR DEVIATION ANALYSIS
            // ============================================================
            var deviationAnalysis = AnalyzeRouteDeviation(
                currentLocation,
                currentRouteSnapshot,
                _rideCache.CurrentRouteIndex,
                currentHeading,
                currentSpeedKmh);

            // Update strike system based on analysis
            UpdateDeviationStrikes(deviationAnalysis);

            // Determine if we're officially off-route (using smart thresholds)
            int strikeThreshold = GetRerouteStrikeThreshold(deviationAnalysis);
            bool officiallyLost = _rideCache.OffRouteStrikeCount >= strikeThreshold;

            // Should we actually reroute?
            bool shouldReroute = ShouldPerformReroute(deviationAnalysis, officiallyLost);

            AppLogger.Info("Routing", 
                $"Analysis: Severity={deviationAnalysis.Severity}, Strikes={_rideCache.OffRouteStrikeCount}/{strikeThreshold}, Reroute={shouldReroute}");

            // [Rest of your existing telemetry calculations...]
            // Distance calculations, ETA, progress, etc.

            return new
            {
                IsOffRoute = officiallyLost,
                ShouldReroute = shouldReroute,
                DeviationSeverity = deviationAnalysis.Severity.ToString(),
                DeviationReason = deviationAnalysis.Reason,
                ConfidenceScore = deviationAnalysis.ConfidenceScore,
                // ... rest of existing fields
            };
        }, _rideCts.Token);

        // Update UI with detailed deviation info
        if (telemetryData.IsOffRoute)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MyDistanceLabel.Text = telemetryData.DeviationReason ?? "Rerouting...";
                MyDistanceLabel.TextColor = telemetryData.DeviationSeverity switch
                {
                    "Minor" => Colors.Orange,
                    "Moderate" => Colors.OrangeRed,
                    "Severe" => Colors.Red,
                    _ => Colors.DodgerBlue
                };
            });
        }

        // PERFORM REROUTE if needed
        if (telemetryData.ShouldReroute)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    Location meetupLoc = _rideCache.ActiveMeetupPoint;
                    string newPolyline = await CalculateAndDrawRoute(
                        currentLocation, 
                        _rideCache.ActiveDestination, 
                        meetupLoc);

                    if (!string.IsNullOrEmpty(newPolyline))
                    {
                        var settings = await _signalRService.GetGroupSettings(GroupNameLabel.Text);
                        if (settings?.EnableDynamicRouting == true)
                        {
                            if (_amIAdmin)
                                await _signalRService.BroadcastLeadRoute(GroupNameLabel.Text, newPolyline);
                            else
                                await _signalRService.ReportRouteDeviation(GroupNameLabel.Text, _myName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Routing", ex, "Failed to recalculate route");
                }
            }, _rideCts.Token);
        }
    }
    catch (OperationCanceledException)
    {
        AppLogger.Info("Telemetry", "Telemetry cancelled");
    }
    catch (Exception ex)
    {
        AppLogger.Error("Telemetry", ex, "CRITICAL ERROR in TrimRouteVisuals");
    }
}
```

---

## Key Improvements Over Current Code

| Feature | Current | New (Google Maps-style) |
|---------|---------|------------------------|
| **Detection Method** | Single distance threshold | Multi-factor (distance + heading + geometry + speed) |
| **Heading Check** | Fixed 60° angle | Speed-adaptive (30° city, 60° highway) |
| **Strike System** | Simple counter | Weighted by severity (1/2/3 strikes) |
| **Reroute Throttle** | Fixed 15s | Adaptive 5-30s based on severity |
| **Confidence Scoring** | None | 0.0-1.0 score system |
| **Alternate Road Detection** | None | Checks for parallel roads |
| **GPS Noise Handling** | Poor | Excellent (speed-based tolerance) |
| **User Feedback** | Generic | Detailed severity-based messages |

---

## Integration Checklist

- [ ] Add `RouteDeviationAnalysis` class to Models
- [ ] Add `DeviationSeverity` enum to Models
- [ ] Implement `AnalyzeRouteDeviation()` method
- [ ] Implement helper methods (bearing, heading normalization, etc.)
- [ ] Update `TrimRouteVisuals()` to use new detection
- [ ] Update RideStateService with new properties if needed
- [ ] Test with 15-20 actual GPS traces
- [ ] Adjust thresholds based on real-world testing
- [ ] Log all rerouting decisions for analytics
