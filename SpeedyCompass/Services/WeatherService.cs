using SpeedyCompass.Engines;
using SpeedyCompass.Shared.Models;
using System.Net.Http.Json;

namespace SpeedyCompass.Services;

public class WeatherService
{
    private readonly HttpClient _httpClient;
    private DateTime _lastWeatherCheckTime = DateTime.MinValue;  

    public WeatherService(IHttpClientFactory httpClientFactory)
    {
        // Ideally, this client should have your JWT token attached so the backend knows who is calling!
        _httpClient = httpClientFactory.CreateClient("CompassBackend");
    }

    public async Task<WeatherAlertResponse> CheckWeatherAtLocationAsync(Location location)
    {
        try
        {
            // THE FIX: Call your backend, not Open-Meteo!
            string url = $"api/weather?lat={location.Latitude}&lng={location.Longitude}";

            var weatherData = await _httpClient.GetFromJsonAsync<WeatherAlertResponse>(url);
            return weatherData;
        }
        catch
        {
            // Fail silently so it doesn't crash the navigation engine if they lose cell service
            return null;
        }
    }

    public async Task StartRadarLoopAsync(
        Func<Location> getCurrentLocation,
        RideStateService rideCache,
        IRoutingEngine routingEngine,
        Action<WeatherAlertResponse> onBadWeatherDetected, // Updated to use the Shared DTO
        CancellationToken cancelToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), cancelToken);
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
                            onBadWeatherDetected?.Invoke(alert);
                        }
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancelToken);
        }
    }
}