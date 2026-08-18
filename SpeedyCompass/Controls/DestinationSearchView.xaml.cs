using System.Text.Json;
using Microsoft.Maui.Devices.Sensors;
using SpeedyCompass.Models;
using SpeedyCompass.Services;

namespace SpeedyCompass.Controls;

public class PlaceSelectedEventArgs : EventArgs
{
    public Location Location { get; set; }
    public string Name { get; set; }
    public List<Location> RouteWaypoints { get; set; } = new(); // NEW: Holds all the intermediate stops!
}

public partial class DestinationSearchView : ContentView
{
    public event EventHandler<PlaceSelectedEventArgs> PreviewRequested;
    public event EventHandler<PlaceSelectedEventArgs> Confirmed;
    public event EventHandler Cleared;

    private static readonly HttpClient _httpClient = new();
    private readonly string _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk";
    private CancellationTokenSource _debounceCts;

    private bool _isInternalUpdate = false;
    private Location _pendingLocation;

    private readonly RideStateService _rideCache;

    public DestinationSearchView()
    {
        InitializeComponent();
        _rideCache = IPlatformApplication.Current?.Services.GetService<RideStateService>();
    }

    public void SetState(bool isVisible, bool isReadOnly, bool showBanner, bool showConfirm)
    {
        this.IsVisible = isVisible;
        DestinationSearchBar.IsReadOnly = isReadOnly;
        InstructionBanner.IsVisible = showBanner;
        ConfirmDestButton.IsVisible = showConfirm;

        if (!isVisible) SuggestionsFrame.IsVisible = false;
    }

    public void InjectExternalSelection(string placeName, Location loc)
    {
        _isInternalUpdate = true;
        DestinationSearchBar.Text = placeName;
        _isInternalUpdate = false;

        _pendingLocation = loc;
        ConfirmDestButton.IsVisible = true;
        ConfirmDestButton.IsEnabled = true;
        ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
        InstructionBanner.IsVisible = false;
    }

    public void Reset()
    {
        _isInternalUpdate = true;
        DestinationSearchBar.Text = string.Empty;
        _isInternalUpdate = false;

        _pendingLocation = null;
        ConfirmDestButton.IsEnabled = false;
        ConfirmDestButton.BackgroundColor = Colors.Gray;
    }

    public void SetDestinationText(string text)
    {
        if (string.IsNullOrEmpty(DestinationSearchBar.Text))
        {
            _isInternalUpdate = true;
            DestinationSearchBar.Text = text;
            _isInternalUpdate = false;
        }
    }
    private async Task<List<Location>> DecodeGoogleMapsMultiStopUrlAsync(string url)
    {
        var waypoints = new List<Location>();

        // Broader coord pattern: supports integer and decimal coordinates
        const string coord = @"-?\d+(?:\.\d+)?";

        // Tracks extraction order from URL text while still de-duping
        var orderedHits = new List<(int Index, double Lat, double Lng)>();

        void AddOrderedWaypoint(double lat, double lng, int indexHint)
        {
            if (double.IsNaN(lat) || double.IsNaN(lng)) return;
            if (Math.Abs(lat) > 90 || Math.Abs(lng) > 180) return;
            if (lat == 0 && lng == 0) return;

            orderedHits.Add((indexHint, lat, lng));
        }

        void FlushOrderedUnique()
        {
            foreach (var hit in orderedHits.OrderBy(h => h.Index))
            {
                if (!waypoints.Any(w =>
                    Math.Abs(w.Latitude - hit.Lat) < 0.0001 &&
                    Math.Abs(w.Longitude - hit.Lng) < 0.0001))
                {
                    waypoints.Add(new Location(hit.Lat, hit.Lng));
                }
            }
        }

        static double ParseInvariant(string value)
            => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            // 1) Expand short links
            if (url.Contains("goo.gl", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("maps.app.goo.gl", StringComparison.OrdinalIgnoreCase))
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var response = await client.GetAsync(url);
                url = response.RequestMessage?.RequestUri?.ToString() ?? url;

                // Meta-refresh trap page fallback
                if (response.Content.Headers.ContentType?.MediaType == "text/html")
                {
                    string html = await response.Content.ReadAsStringAsync();
                    var metaMatch = System.Text.RegularExpressions.Regex.Match(html, @"(?:url|URL)=([^""'>]+)");
                    if (metaMatch.Success)
                    {
                        string metaUrl = metaMatch.Groups[1].Value.Replace("&amp;", "&");
                        if (metaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            url = metaUrl;
                    }
                }
            }

            // Parse both encoded and decoded views
            string decodedUrl = Uri.UnescapeDataString(url);

            void ExtractFrom(string source)
            {
                // A) !3dLAT!4dLNG (pin-style)
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(source, $@"!3d({coord})!4d({coord})"))
                {
                    AddOrderedWaypoint(ParseInvariant(m.Groups[1].Value), ParseInvariant(m.Groups[2].Value), m.Index);
                }

                // B) !1dLNG!2dLAT (route-style)
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(source, $@"!1d({coord})!2d({coord})"))
                {
                    AddOrderedWaypoint(ParseInvariant(m.Groups[2].Value), ParseInvariant(m.Groups[1].Value), m.Index);
                }

                // C) /LAT,LNG in path (but ignore /@ viewport marker here)
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(source, $@"/({coord}),({coord})(?=/|$)"))
                {
                    int atPos = source.LastIndexOf("/@", m.Index, StringComparison.Ordinal);
                    if (atPos >= 0 && atPos == m.Index - 2) continue;

                    AddOrderedWaypoint(ParseInvariant(m.Groups[1].Value), ParseInvariant(m.Groups[2].Value), m.Index);
                }

                // D) query params: q=, origin=, destination=, saddr=, daddr=
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(source, $@"(?:[?&](?:q|origin|destination|saddr|daddr)=)({coord}),({coord})"))
                {
                    AddOrderedWaypoint(ParseInvariant(m.Groups[1].Value), ParseInvariant(m.Groups[2].Value), m.Index);
                }
            }

            ExtractFrom(url);
            ExtractFrom(decodedUrl);

            FlushOrderedUnique();

            // E) Last fallback: viewport center @lat,lng
            if (waypoints.Count == 0)
            {
                var mapMatch = System.Text.RegularExpressions.Regex.Match(decodedUrl, $@"@({coord}),({coord})");
                if (mapMatch.Success)
                {
                    waypoints.Add(new Location(
                        ParseInvariant(mapMatch.Groups[1].Value),
                        ParseInvariant(mapMatch.Groups[2].Value)));
                }
            }

            return waypoints;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"URL Decode Error: {ex.Message}");
            return waypoints;
        }
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalUpdate) return;
        if (e.OldTextValue == e.NewTextValue) return;

        string query = e.NewTextValue?.Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            Cleared?.Invoke(this, EventArgs.Empty);
            return;
        }

        InstructionBanner.IsVisible = false;

        if (query.StartsWith("http://") || query.StartsWith("https://"))
        {
            _debounceCts?.Cancel();
            SuggestionsFrame.IsVisible = false;
            ConfirmDestButton.IsEnabled = false;

            // Fetch the list of stops!
            var points = await DecodeGoogleMapsMultiStopUrlAsync(query);

            if (points != null && points.Count > 0)
            {
                _pendingLocation = points.Last(); // The final destination is always the last point

                _isInternalUpdate = true;
                DestinationSearchBar.Text = points.Count > 1 ? "Shared Multi-Stop Route" : "Shared Map Location";
                _isInternalUpdate = false;

                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;
                ConfirmDestButton.IsVisible = true;

                // Pass the ENTIRE list of stops to the UI!
                PreviewRequested?.Invoke(this, new PlaceSelectedEventArgs
                {
                    Location = _pendingLocation,
                    Name = DestinationSearchBar.Text,
                    RouteWaypoints = points
                });
            }
            return;
        }

        // --- Standard Google Places API Search ---
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(500, token);
            if (token.IsCancellationRequested) return;

            var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

            object reqBody;
            var lastLoc = _rideCache?.LastOdometerLocation;

            if (lastLoc != null && lastLoc.Latitude != 0)
            {
                reqBody = new
                {
                    input = query,
                    locationBias = new
                    {
                        circle = new
                        {
                            center = new { latitude = lastLoc.Latitude, longitude = lastLoc.Longitude },
                            radius = 100000.0 // 100km radius
                        }
                    }
                };
            }
            else
            {
                reqBody = new { input = query };
            }

            request.Content = new StringContent(JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<AutocompleteResponse>(responseBody);

            if (token.IsCancellationRequested) return;

            if (result != null && result.Suggestions != null && result.Suggestions.Any())
            {
                var displayList = result.Suggestions
                    .Where(s => s.PlacePrediction != null)
                    .Select(s => new { Description = s.PlacePrediction.Text.Text, PlaceId = s.PlacePrediction.PlaceId })
                    .ToList();

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SuggestionsListView.ItemsSource = displayList;
                    SuggestionsFrame.IsVisible = true;
                });
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => SuggestionsFrame.IsVisible = false);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}"); }
    }

    private async void OnSuggestionSelected(object sender, SelectedItemChangedEventArgs e)
    {
        if (e.SelectedItem == null) return;
        var selectedPlace = (dynamic)e.SelectedItem;
        string desc = selectedPlace.Description;
        string placeId = selectedPlace.PlaceId;

        SuggestionsFrame.IsVisible = false;
        _isInternalUpdate = true;
        DestinationSearchBar.Text = desc;
        _isInternalUpdate = false;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://places.googleapis.com/v1/places/{placeId}");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
            request.Headers.Add("X-Goog-FieldMask", "location");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var details = JsonSerializer.Deserialize<PlaceDetailsResponse>(responseBody);

            if (details?.Location != null)
            {
                _pendingLocation = new Location(details.Location.Latitude, details.Location.Longitude);
                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;

                PreviewRequested?.Invoke(this, new PlaceSelectedEventArgs { Location = _pendingLocation, Name = desc });
            }
        }
        catch (Exception) { /* Handle error */ }

        SuggestionsListView.SelectedItem = null;
    }

    private async void OnSearchPressed(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DestinationSearchBar.Text)) return;

        // Prevent running standard geocoding on a URL if the user hits "Enter" manually
        if (DestinationSearchBar.Text.StartsWith("http://") || DestinationSearchBar.Text.StartsWith("https://")) return;

        try
        {
            var locations = await Geocoding.Default.GetLocationsAsync(DestinationSearchBar.Text);
            var location = locations?.FirstOrDefault();
            if (location != null)
            {
                _pendingLocation = location;
                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;

                PreviewRequested?.Invoke(this, new PlaceSelectedEventArgs { Location = _pendingLocation, Name = DestinationSearchBar.Text });
            }
        }
        catch (Exception) { }
    }

    private void OnConfirmDestinationClicked(object sender, EventArgs e)
    {
        if (_pendingLocation == null || string.IsNullOrWhiteSpace(DestinationSearchBar.Text)) return;

        Confirmed?.Invoke(this, new PlaceSelectedEventArgs { Location = _pendingLocation, Name = DestinationSearchBar.Text });
        ConfirmDestButton.IsVisible = false;
    }
}