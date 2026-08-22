using SpeedyCompass.Engines;
using SpeedyCompass.Services;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

public class WeatherAlert
{
    public bool IsBadWeather { get; set; }
    public string WarningMessage { get; set; }
    public string IconEmoji { get; set; }
    public string AlertColor { get; set; } // e.g., "#FF4500" for Red/Orange warnings
}

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
}

public class WeatherService
{
    private readonly HttpClient _httpClient;
    private DateTime _lastWeatherCheckTime = DateTime.MinValue;

    public WeatherService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("weatherapi");
    }

    public async Task<WeatherAlert> CheckWeatherAtLocationAsync(Location location)
    {
        try
        {
            var weatherData = await _httpClient.GetFromJsonAsync<OpenMeteoResponse>($"v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&current=precipitation,weather_code") ?? new OpenMeteoResponse();

            if (weatherData?.Current == null) return null;

            return ParseWmoCode(weatherData.Current.WeatherCode, weatherData.Current.Precipitation);
        }
        catch
        {
            // Fail silently so it doesn't crash the navigation engine if they lose cell service
            return null;
        }
    }

    private WeatherAlert ParseWmoCode(int wmoCode, double precipitationLevel)
    {
        var alert = new WeatherAlert { IsBadWeather = true };

        switch (wmoCode)
        {
            // Rain & Drizzle
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
                alert.AlertColor = "#1E90FF"; // DodgerBlue
                break;

            // Thunderstorms
            case 95:
            case 96:
            case 99:
                alert.WarningMessage = "Thunderstorm ahead! Seek shelter.";
                alert.IconEmoji = "⛈️";
                alert.AlertColor = "#FF4500"; // OrangeRed
                break;

            // Snow / Freezing Rain (Dangerous for bikes)
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
                alert.WarningMessage = "Freezing conditions/Snow ahead";
                alert.IconEmoji = "❄️";
                alert.AlertColor = "#00CED1"; // DarkTurquoise
                break;

            // Fog / Poor Visibility
            case 45:
            case 48:
                alert.WarningMessage = "Dense fog ahead. Reduced visibility.";
                alert.IconEmoji = "🌫️";
                alert.AlertColor = "#808080"; // Gray
                break;

            // Clear / Cloudy (No alert needed)
            default:
                alert.IsBadWeather = false;
                break;
        }

        return alert;
    }
    public async Task StartRadarLoopAsync(
        Func<Location> getCurrentLocation,
        RideStateService rideCache,
        IRoutingEngine routingEngine,
        Action<WeatherAlert> onBadWeatherDetected,
        CancellationToken cancelToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), cancelToken);
        // Reset the clock every time a new ride starts so it pings immediately!
        _lastWeatherCheckTime = DateTime.MinValue;

        while (!cancelToken.IsCancellationRequested)
        {
            bool isWeatherEnabled = Preferences.Default.Get("Map_WeatherRadarEnabled", true);

            if (isWeatherEnabled && rideCache?.CurrentRoutePoints != null && rideCache.CurrentRoutePoints.Any())
            {
                double lookAheadKm = Preferences.Default.Get("Map_WeatherLookAheadKm", 15.0);

                var currentLocation = getCurrentLocation();
                double currentSpeedKmh = (currentLocation?.Speed ?? 0) * 3.6;
                double effectiveSpeed = Math.Max(currentSpeedKmh, 40.0);

                double timeToReachTargetHours = lookAheadKm / effectiveSpeed;
                double rawDelayMinutes = (timeToReachTargetHours * 60.0) * 0.8;
                double requiredDelayMinutes = Math.Clamp(rawDelayMinutes, 3.0, 30.0);

                if ((DateTime.Now - _lastWeatherCheckTime).TotalMinutes >= requiredDelayMinutes)
                {
                    _lastWeatherCheckTime = DateTime.Now;

                    var checkPoint = routingEngine.GetLocationAheadOnRoute(
                        rideCache.CurrentRoutePoints,
                        rideCache.CurrentRouteIndex,
                        lookAheadKm);

                    if (checkPoint != null)
                    {
                        var alert = await CheckWeatherAtLocationAsync(checkPoint);

                        if (alert != null && alert.IsBadWeather)
                        {
                            // UI + voice are now handled by caller plug-in pipeline
                            onBadWeatherDetected?.Invoke(alert);
                        }
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancelToken);
        }
    }
}