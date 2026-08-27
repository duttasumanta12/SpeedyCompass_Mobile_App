using Microsoft.Extensions.Caching.Memory;
using SpeedyCompass.Shared.Models;
using System.Text.Json.Serialization;

namespace SpeedyCompass.Backend.Services.External;

// Internal classes to catch the Open-Meteo JSON response
internal class OpenMeteoResponse
{
    [JsonPropertyName("current")]
    public CurrentWeather Current { get; set; }
}

internal class CurrentWeather
{
    [JsonPropertyName("weather_code")]
    public int WeatherCode { get; set; }

    [JsonPropertyName("precipitation")]
    public double Precipitation { get; set; }

    [JsonPropertyName("visibility")]
    public double VisibilityMeters { get; set; }
}

public class WeatherGatewayService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WeatherGatewayService> _logger;

    public WeatherGatewayService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<WeatherGatewayService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("weatherapi");
        _cache = cache;
        _logger = logger;
    }

    public async Task<WeatherAlertResponse?> GetWeatherForCorridorAsync(double lat, double lng)
    {
        // 1. SPATIAL CACHING: Round coordinates to create a 1.1 km² grid box
        double gridLat = Math.Round(lat, 2);
        double gridLng = Math.Round(lng, 2);
        string cacheKey = $"weather_grid_{gridLat}_{gridLng}";

        if (_cache.TryGetValue(cacheKey, out WeatherAlertResponse? cachedAlert))
        {
            _logger.LogInformation($"Served Weather from Spatial Cache for {gridLat}, {gridLng}");
            return cachedAlert;
        }

        try
        {
            string url = $"v1/forecast?latitude={lat}&longitude={lng}&current=precipitation,weather_code,visibility";
            var response = await _httpClient.GetFromJsonAsync<OpenMeteoResponse>(url);

            if (response?.Current == null) return null;

            var alert = ParseWeatherMetrics(response.Current.WeatherCode, response.Current.Precipitation, response.Current.VisibilityMeters);

            // Cache for 15 minutes to save API bandwidth
            _cache.Set(cacheKey, alert, TimeSpan.FromMinutes(15));
            return alert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch weather from Open-Meteo.");
            return null;
        }
    }

    private WeatherAlertResponse ParseWeatherMetrics(int wmoCode, double precipitationLevel, double visibilityMeters)
    {
        var alert = new WeatherAlertResponse { IsBadWeather = true };

        if (visibilityMeters > 0 && visibilityMeters <= 500)
        {
            alert.WarningMessage = $"Dense fog hazard ahead. Visibility dropped to {Math.Round(visibilityMeters)} meters.";
            alert.IconEmoji = "🌫️";
            alert.AlertColor = "#FF4500";
            return alert;
        }
        else if (visibilityMeters > 500 && visibilityMeters <= 1000)
        {
            alert.WarningMessage = $"Low visibility ahead. Prepare for mist or haze.";
            alert.IconEmoji = "🌫️";
            alert.AlertColor = "#FFA500";
            return alert;
        }

        switch (wmoCode)
        {
            case 51:
            case 53:
            case 55:
            case 61:
            case 63:
            case 65:
            case 80:
            case 81:
            case 82:
                alert.WarningMessage = precipitationLevel > 2.0 ? "Heavy rain ahead" : "Light rain ahead";
                alert.IconEmoji = "🌧️";
                alert.AlertColor = "#1E90FF";
                break;
            case 95:
            case 96:
            case 99:
                alert.WarningMessage = "Thunderstorm ahead! Seek shelter.";
                alert.IconEmoji = "⛈️";
                alert.AlertColor = "#FF4500";
                break;
            case 56:
            case 57:
            case 66:
            case 67:
            case 71:
            case 73:
            case 75:
            case 77:
            case 85:
            case 86:
                alert.WarningMessage = "Freezing conditions or snow ahead.";
                alert.IconEmoji = "❄️";
                alert.AlertColor = "#00CED1";
                break;
            case 45:
            case 48:
                alert.WarningMessage = "Foggy conditions ahead.";
                alert.IconEmoji = "🌫️";
                alert.AlertColor = "#808080";
                break;
            default:
                alert.IsBadWeather = false;
                break;
        }

        return alert;
    }
}