namespace SpeedyCompass.Shared.Models;

public class RouteRequestDto
{
    public double OriginLat { get; set; }
    public double OriginLng { get; set; }
    public int? OriginHeading { get; set; }
    public double DestLat { get; set; }
    public double DestLng { get; set; }
    public double? MeetupLat { get; set; }
    public double? MeetupLng { get; set; }
    public bool IncludeVoiceSteps { get; set; }
    public bool IsPreview { get; set; }
    public string Tier { get; set; } = "Free";
}
