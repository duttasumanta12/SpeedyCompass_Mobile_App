namespace SpeedyCompass.Shared.Models;

public class WeatherAlertResponse
{
    public bool IsBadWeather { get; set; }
    public string WarningMessage { get; set; }
    public string IconEmoji { get; set; }
    public string AlertColor { get; set; } // e.g., "#FF4500" for Red/Orange warnings
}