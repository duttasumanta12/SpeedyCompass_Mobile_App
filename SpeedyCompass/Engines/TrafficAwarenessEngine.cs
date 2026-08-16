using System;
using System.Collections.Generic;
using System.Linq;

namespace SpeedyCompass.Engines;

public class TrafficAnalysis
{
    public bool ShouldAlert { get; set; }
    public string Message { get; set; }
    public double RiskScore { get; set; }
}

public class TrafficAwarenessEngine
{
    private double _emaRisk;
    private const double Alpha = 0.35;

    public TrafficAnalysis AnalyzeTrafficAhead(
        int currentIndex,
        List<Location> polylinePoints,
        List<SpeedInterval> trafficData,
        double speedKmh,
        DateTime lastTrafficAlertTime)
    {
        var result = new TrafficAnalysis();
        if (polylinePoints == null || polylinePoints.Count < 2 || trafficData == null || trafficData.Count == 0)
            return result;

        int endIndex = GetLookAheadEndIndex(currentIndex, polylinePoints, speedKmh);
        if (endIndex <= currentIndex) return result;

        var window = trafficData
            .Where(i => i.EndPolylinePointIndex >= currentIndex && i.StartPolylinePointIndex <= endIndex)
            .ToList();

        if (!window.Any()) return result;

        double weightedRisk = 0;
        double weightedPoints = 0;
        int nearestStart = int.MaxValue;

        foreach (var i in window)
        {
            int s = Math.Max(currentIndex, i.StartPolylinePointIndex);
            int e = Math.Min(endIndex, i.EndPolylinePointIndex);
            int points = Math.Max(0, e - s + 1);
            if (points == 0) continue;

            double risk = i.Speed switch
            {
                "TRAFFIC_JAM" => 1.0,
                "SLOW" => 0.45,
                _ => 0.0
            };

            weightedRisk += risk * points;
            weightedPoints += points;

            if (risk > 0 && i.StartPolylinePointIndex < nearestStart)
                nearestStart = i.StartPolylinePointIndex;
        }

        if (weightedPoints <= 0) return result;

        double instantRisk = weightedRisk / weightedPoints;
        _emaRisk = (Alpha * instantRisk) + ((1 - Alpha) * _emaRisk);
        result.RiskScore = _emaRisk;

        var now = DateTime.Now;
        bool cooldownOk = (now - lastTrafficAlertTime).TotalSeconds > 90;
        if (!cooldownOk || _emaRisk < 0.2 || nearestStart == int.MaxValue) return result;

        double distKm = ComputeDistanceKm(currentIndex, nearestStart, polylinePoints);
        if (_emaRisk >= 0.75)
            result.Message = $"Heavy traffic in {distKm:F1} km. Expect major slowdown.";
        else if (_emaRisk >= 0.45)
            result.Message = $"Congestion in {distKm:F1} km. Prepare to slow down.";
        else
            result.Message = $"Slow traffic in {distKm:F1} km ahead.";

        result.ShouldAlert = true;
        return result;
    }

    private static int GetLookAheadEndIndex(int currentIndex, List<Location> points, double speedKmh)
    {
        double lookAheadKm = speedKmh switch
        {
            < 20 => 1.2,
            < 50 => 2.0,
            < 90 => 3.0,
            _ => 4.5
        };

        double acc = 0;
        for (int i = currentIndex; i < points.Count - 1; i++)
        {
            acc += Location.CalculateDistance(points[i], points[i + 1], DistanceUnits.Kilometers);
            if (acc >= lookAheadKm) return i + 1;
        }
        return points.Count - 1;
    }

    private static double ComputeDistanceKm(int startIndex, int targetIndex, List<Location> points)
    {
        if (targetIndex <= startIndex) return 0;
        double km = 0;
        for (int i = startIndex; i < targetIndex && i < points.Count - 1; i++)
            km += Location.CalculateDistance(points[i], points[i + 1], DistanceUnits.Kilometers);
        return km;
    }
}