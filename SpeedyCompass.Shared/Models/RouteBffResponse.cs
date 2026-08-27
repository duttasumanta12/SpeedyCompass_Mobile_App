namespace SpeedyCompass.Shared.Models;

public class RouteBffResponse
{
    public string EncodedPolyline { get; set; }
    public double DistanceKm { get; set; }
    public string EtaText { get; set; }
    public List<RouteStepDto> VoiceSteps { get; set; } = new();
    public List<SpeedIntervalDto> TrafficData { get; set; } = new();
}
