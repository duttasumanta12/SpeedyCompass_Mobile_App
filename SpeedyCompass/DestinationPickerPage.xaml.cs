using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using SpeedyCompass.Services;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeedyCompass;

// 1. Models to deserialize the Google Places API (New) JSON responses
public class AutocompleteRequest
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;
}

public class AutocompleteResponse
{
    [JsonPropertyName("suggestions")]
    public List<Suggestion> Suggestions { get; set; } = new();
}

public class Suggestion
{
    [JsonPropertyName("placePrediction")]
    public PlacePrediction PlacePrediction { get; set; }
}

public class PlacePrediction
{
    [JsonPropertyName("placeId")]
    public string PlaceId { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public PlaceText Text { get; set; }
}

public class PlaceText
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class PlaceDetailsResponse
{
    [JsonPropertyName("location")]
    public PlaceLocation Location { get; set; }
}

public class PlaceLocation
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

// A simplified model for our UI DataBinding
public class UIPlaceSuggestion
{
    public string Description { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
}

public partial class DestinationPickerPage : ContentPage
{
    private readonly SignalRService _signalRService;
    private readonly string _groupName;
    private Location _selectedLocation;

    // Securely loaded API key
    private readonly string _googleApiKey;

    // HTTP Client to call Google APIs
    private static readonly HttpClient _httpClient = new();

    public DestinationPickerPage(SignalRService signalRService, string groupName)
    {
        InitializeComponent();
        _signalRService = signalRService;
        _groupName = groupName;

        // Read the credential from the User Secrets configuration
        _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk"
            ?? throw new ArgumentNullException("GoogleApiKey not found in User Secrets configuration.");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await CenterMapOnUserLocation();
    }

    private async Task CenterMapOnUserLocation()
    {
        try
        {
            // Fetch the user's current location (Medium accuracy is fine just to center the map)
            var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10));
            var location = await Geolocation.Default.GetLocationAsync(request);

            if (location != null)
            {
                // Move the map camera to show a 2-mile radius around the user
                var mapSpan = MapSpan.FromCenterAndRadius(location, Distance.FromMiles(2));
                PreviewMap.MoveToRegion(mapSpan);
            }
        }
        catch (Exception ex)
        {
            // Silently handle if permissions are denied or GPS is disabled
            System.Diagnostics.Debug.WriteLine($"Failed to get initial location: {ex.Message}");
        }
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = e.NewTextValue;

        // Don't search until they've typed at least 3 characters
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
        {
            // Update: Toggle the Frame instead of the ListView
            SuggestionsFrame.IsVisible = false;
            return;
        }

        try
        {
            // Call Google Places API (New) - Autocomplete endpoint
            var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

            var reqBody = new AutocompleteRequest { Input = query };
            request.Content = new StringContent(JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<AutocompleteResponse>(responseBody);

            if (result != null && result.Suggestions != null && result.Suggestions.Any())
            {
                // Map to our UI model so XAML binding still works perfectly
                var displayList = result.Suggestions
                    .Where(s => s.PlacePrediction != null)
                    .Select(s => new UIPlaceSuggestion
                    {
                        Description = s.PlacePrediction.Text.Text,
                        PlaceId = s.PlacePrediction.PlaceId
                    }).ToList();

                SuggestionsListView.ItemsSource = displayList;

                // Update: Toggle the Frame instead of the ListView
                SuggestionsFrame.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
        }
    }

    // UPDATED: Changed SelectionChangedEventArgs to SelectedItemChangedEventArgs for ListView compatibility
    private async void OnSuggestionSelected(object sender, SelectedItemChangedEventArgs e)
    {
        // UPDATED: Use e.SelectedItem instead of e.CurrentSelection
        if (e.SelectedItem is UIPlaceSuggestion selectedPlace)
        {
            // 1. Hide the suggestions dropdown frame and update the search bar text
            SuggestionsFrame.IsVisible = false;
            DestinationSearchBar.Text = selectedPlace.Description;

            try
            {
                // 2. Fetch the exact coordinates using the Place API (New)
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://places.googleapis.com/v1/places/{selectedPlace.PlaceId}");
                request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

                // FieldMask is REQUIRED in the New API to tell Google exactly what data you want to retrieve
                request.Headers.Add("X-Goog-FieldMask", "location");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                var details = JsonSerializer.Deserialize<PlaceDetailsResponse>(responseBody);

                if (details?.Location != null)
                {
                    double lat = details.Location.Latitude;
                    double lng = details.Location.Longitude;

                    _selectedLocation = new Location(lat, lng);

                    // 3. Clear old preview pins
                    PreviewMap.Pins.Clear();

                    // 4. Add new pin to map
                    var pin = new Pin
                    {
                        Label = selectedPlace.Description,
                        Type = PinType.Place,
                        Location = _selectedLocation
                    };
                    PreviewMap.Pins.Add(pin);

                    // 5. Move map camera view to focus on the destination
                    var mapSpan = MapSpan.FromCenterAndRadius(_selectedLocation, Distance.FromMiles(1));
                    PreviewMap.MoveToRegion(mapSpan);

                    // 6. Enable the broadcast button
                    StartNavButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", "Could not fetch location details.", "OK");
                System.Diagnostics.Debug.WriteLine($"Details Error: {ex.Message}");
            }

            // Clear selection so the user can tap it again if needed
            SuggestionsListView.SelectedItem = null;
        }
    }

    private async void OnStartNavigationClicked(object sender, EventArgs e)
    {
        if (_selectedLocation == null) return;

        StartNavButton.IsEnabled = false;

        try
        {
            // Broadcast using our clean service wrapper
            await _signalRService.StartGroupNavigation(
                _groupName,
                _selectedLocation.Latitude,
                _selectedLocation.Longitude,
                DestinationSearchBar.Text);

            // Navigate the Admin directly to their own live mapping screen
            await Navigation.PushAsync(new ActiveMapPage(_signalRService, _groupName, _selectedLocation, DestinationSearchBar.Text));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", "Could not synchronize with the group server: " + ex.Message, "OK");
            StartNavButton.IsEnabled = true;
        }
    }
}