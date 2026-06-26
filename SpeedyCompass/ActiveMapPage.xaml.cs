using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Extensions.Configuration;
using SpeedyCompass.Services;
using SpeedyCompass.Controls;
using Microsoft.Maui.ApplicationModel;

namespace SpeedyCompass;

// Models to deserialize the Google Routes API JSON responses
public class RoutesRequest { [JsonPropertyName("origin")] public RouteWaypoint Origin { get; set; } [JsonPropertyName("destination")] public RouteWaypoint Destination { get; set; } [JsonPropertyName("travelMode")] public string TravelMode { get; set; } = "DRIVE"; }
public class RouteWaypoint { [JsonPropertyName("location")] public RouteLocation Location { get; set; } }
public class RouteLocation { [JsonPropertyName("latLng")] public RouteLatLng LatLng { get; set; } }
public class RouteLatLng { [JsonPropertyName("latitude")] public double Latitude { get; set; } [JsonPropertyName("longitude")] public double Longitude { get; set; } }
public class RoutesResponse { [JsonPropertyName("routes")] public List<RouteData> Routes { get; set; } }
public class RouteData { [JsonPropertyName("distanceMeters")] public int DistanceMeters { get; set; } [JsonPropertyName("duration")] public string Duration { get; set; } [JsonPropertyName("polyline")] public RoutePolyline Polyline { get; set; } }
public class RoutePolyline { [JsonPropertyName("encodedPolyline")] public string EncodedPolyline { get; set; } }

// UI Model for the interactive banners
public class RiderBanner : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; }
    public Location Location { get; set; }
    public DateTime LastUpdate { get; set; }

    private double _speedMph;
    public double SpeedMph
    {
        get => _speedMph;
        set { _speedMph = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedDisplay)); }
    }

    public string SpeedDisplay => $"{Math.Round(SpeedMph)} mph";

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

public partial class ActiveMapPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly string _groupName;
    private readonly Location _destination;
    private readonly string _googleApiKey;
    private static readonly HttpClient _httpClient = new();

    // Thread-safe dictionaries for data management
    private readonly ConcurrentDictionary<string, RiderBanner> _riderData = new();
    public ObservableCollection<RiderBanner> RidersRoster { get; set; } = new();

    private bool _isTracking = false;
    private Location _lastKnownLocation;
    private CancellationTokenSource _pressCts;

    // Track the local user's custom pin
    private RiderPin _myPin;

    public ActiveMapPage(SignalRService signalRService, string groupName, Location destination, string destinationName)
    {
        InitializeComponent();

        _signalRService = signalRService;
        _groupName = groupName;
        _destination = destination;
        Title = $"Route to {destinationName}";

        //var config = Application.Current?.MainPage?.Handler?.MauiContext?.Services?.GetService<IConfiguration>();
        _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk" ?? throw new Exception("API Key missing");

        _signalRService.RiderLocationUpdated += OnRiderLocationUpdated;

        InitializeNavigationAsync();

        NavMap.Pins.
    }

    private async void InitializeNavigationAsync()
    {
        try
        {
            var locationRequest = new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(10));
            var currentLocation = await Geolocation.Default.GetLocationAsync(locationRequest);

            if (currentLocation != null)
            {
                // Add the custom "You" pin immediately upon load instead of waiting for the tracking loop
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (_myPin == null)
                    {
                        _myPin = new RiderPin { Label = "You",  Username = "You", Speed = "0 mph", Location = currentLocation };
                        
                        _myPin.ImageSource = ImageSource.FromFile("custom_pin.svg"); // Ensure this SVG is included in your project resources

                        NavMap.Pins.Add(_myPin);
                    }
                });

                await CalculateAndDrawRoute(currentLocation, _destination);

                // Zoom out slightly to show surrounding area
                var mapSpan = MapSpan.FromCenterAndRadius(currentLocation, Distance.FromMiles(5));
                NavMap.MoveToRegion(mapSpan);

                StartLocalTracking();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation Init Error: {ex.Message}");
        }
    }

    private async Task CalculateAndDrawRoute(Location origin, Location dest)
    {
        try
        {
            var requestBody = new RoutesRequest
            {
                Origin = new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = origin.Latitude, Longitude = origin.Longitude } } },
                Destination = new RouteWaypoint { Location = new RouteLocation { LatLng = new RouteLatLng { Latitude = dest.Latitude, Longitude = dest.Longitude } } }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
            request.Headers.Add("X-Goog-FieldMask", "routes.polyline.encodedPolyline");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var routeResult = JsonSerializer.Deserialize<RoutesResponse>(json);

            var mainRoute = routeResult?.Routes?.FirstOrDefault();
            if (mainRoute != null)
            {
                DrawPolyline(mainRoute.Polyline.EncodedPolyline);
                NavMap.Pins.Add(new Pin { Label = "Destination", Type = PinType.Place, Location = _destination });
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Routing Error: {ex.Message}"); }
    }

    private void DrawPolyline(string encodedPolyline)
    {
        if (string.IsNullOrEmpty(encodedPolyline)) return;
        var polyline = new Polyline { StrokeColor = Colors.DodgerBlue, StrokeWidth = 8 };
        foreach (var coord in DecodeGooglePolyline(encodedPolyline)) { polyline.Geopath.Add(coord); }
        NavMap.MapElements.Add(polyline);
    }

    private List<Location> DecodeGooglePolyline(string encodedPoints)
    {
        var poly = new List<Location>();
        char[] polyChars = encodedPoints.ToCharArray();
        int index = 0, currentLat = 0, currentLng = 0;

        while (index < polyChars.Length)
        {
            int sum = 0, shifter = 0, b;
            do { b = polyChars[index++] - 63; sum |= (b & 31) << shifter; shifter += 5; } while (b >= 32);
            currentLat += ((sum & 1) == 1 ? ~(sum >> 1) : (sum >> 1));

            sum = 0; shifter = 0;
            do { b = polyChars[index++] - 63; sum |= (b & 31) << shifter; shifter += 5; } while (b >= 32);
            currentLng += ((sum & 1) == 1 ? ~(sum >> 1) : (sum >> 1));

            poly.Add(new Location(currentLat / 100000.0, currentLng / 100000.0));
        }
        return poly;
    }

    private async void StartLocalTracking()
    {
        _isTracking = true;
        while (_isTracking)
        {
            try
            {
                var location = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(5)));
                if (location != null)
                {
                    double speedMph = (location.Speed ?? 0) * 2.23694;

                    // Base minimum distance is 5 meters. Add 1 meter of leeway for every 1 mph of speed.
                    double dynamicDistanceThresholdMeters = 5 + speedMph;

                    double distanceMovedMeters = _lastKnownLocation == null
                        ? double.MaxValue
                        : Location.CalculateDistance(_lastKnownLocation, location, DistanceUnits.Kilometers) * 1000;

                    // 1. Update the Local "You" Pin dynamically
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_myPin == null)
                        {
                            _myPin = new RiderPin { Username = "You", Speed = $"{Math.Round(speedMph)} mph", Location = location };
                            NavMap.Pins.Add(_myPin);
                        }
                        else
                        {
                            // Hack for custom native handlers: We must remove & re-add the pin 
                            // to force the MapHandler to trigger 'MapPinsWithCustomViews' and redraw the text.
                            NavMap.Pins.Remove(_myPin);
                            _myPin.Location = location;
                            _myPin.Speed = $"{Math.Round(speedMph)} mph";
                            NavMap.Pins.Add(_myPin);
                        }
                    });

                    // 2. Only broadcast to SignalR if the rider has moved past the dynamic threshold
                    if (distanceMovedMeters >= dynamicDistanceThresholdMeters)
                    {
                        _lastKnownLocation = location;
                        await _signalRService.UpdateLocation(_groupName, location.Latitude, location.Longitude, location.Course ?? 0);
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Tracking Error: {ex.Message}"); }

            await Task.Delay(2000);
        }
    }

    private void OnRiderLocationUpdated(string riderId, double lat, double lng, double heading)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var now = DateTime.UtcNow;
            var newLoc = new Location(lat, lng);

            if (_riderData.TryGetValue(riderId, out var existingRider))
            {
                // Calculate speed locally 
                double distanceMiles = Location.CalculateDistance(existingRider.Location, newLoc, DistanceUnits.Miles);
                double hours = (now - existingRider.LastUpdate).TotalHours;
                double newSpeed = hours > 0 ? distanceMiles / hours : 0;

                if (newSpeed < 150) existingRider.SpeedMph = newSpeed;

                existingRider.Location = newLoc;
                existingRider.LastUpdate = now;

                // Update their custom map pin
                var pin = NavMap.Pins.FirstOrDefault(p => p is RiderPin rp && rp.Username == riderId) as RiderPin;
                if (pin != null)
                {
                    // Remove and Re-add forces the Android handler to regenerate the bitmap with the new speed
                    NavMap.Pins.Remove(pin);
                    pin.Location = newLoc;
                    pin.Speed = $"{Math.Round(existingRider.SpeedMph)} mph";
                    NavMap.Pins.Add(pin);
                }
            }
            else
            {
                // New rider joined the map
                var newRider = new RiderBanner { Name = riderId, Location = newLoc, LastUpdate = now, SpeedMph = 0 };
                _riderData.TryAdd(riderId, newRider);
                RidersRoster.Add(newRider);

                var newPin = new RiderPin { Username = riderId, Speed = "0 mph", Location = newLoc };
                NavMap.Pins.Add(newPin);
            }
        });
    }

    // --- NAVIGATION HANDOFF ---
    private async void OnLaunchNativeNavClicked(object sender, EventArgs e)
    {
        try
        {
            if (DeviceInfo.Platform == DevicePlatform.Android)
            {
                await Launcher.OpenAsync($"google.navigation:q={_destination.Latitude},{_destination.Longitude}&mode=d");
            }
            else if (DeviceInfo.Platform == DevicePlatform.iOS)
            {
                bool hasGoogleMaps = await Launcher.TryOpenAsync($"comgooglemaps://?daddr={_destination.Latitude},{_destination.Longitude}&directionsmode=driving");
                if (!hasGoogleMaps)
                {
                    await Launcher.OpenAsync($"http://maps.apple.com/?daddr={_destination.Latitude},{_destination.Longitude}&dirflg=d");
                }
            }
        }
        catch (Exception ex) { await DisplayAlert("Navigation Error", "Could not open map.", "OK"); }
    }

    // --- 3-SECOND HOLD LOGIC FOR BANNERS ---
    private async void OnBannerPressed(object sender, EventArgs e)
    {
        _pressCts = new CancellationTokenSource();
        var button = sender as Button;

        if (button?.CommandParameter is RiderBanner rider)
        {
            try
            {
                await Task.Delay(3000, _pressCts.Token);
                ShowDetailedPopup(rider);
            }
            catch (TaskCanceledException) { /* User lifted finger early */ }
        }
    }

    private void OnBannerReleased(object sender, EventArgs e)
    {
        _pressCts?.Cancel();
    }

    private void ShowDetailedPopup(RiderBanner rider)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PopupNameLabel.Text = rider.Name;
            PopupSpeedLabel.Text = $"{Math.Round(rider.SpeedMph)} mph";
            PopupLocationLabel.Text = $"{Math.Round(rider.Location.Latitude, 4)}, {Math.Round(rider.Location.Longitude, 4)}";
            PopupAltitudeLabel.Text = "Requires Backend Update";

            DetailsPopup.IsVisible = true;
        });
    }

    private void OnClosePopupClicked(object sender, EventArgs e)
    {
        DetailsPopup.IsVisible = false;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isTracking = false;
        _signalRService.RiderLocationUpdated -= OnRiderLocationUpdated;
    }
}