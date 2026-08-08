using System.Text.Json;
using Microsoft.Maui.Devices.Sensors;
using SpeedyCompass.Models; // Assumes your Places API models are here

namespace SpeedyCompass.Controls;

public class PlaceSelectedEventArgs : EventArgs { public Location Location { get; set; } public string Name { get; set; } }

public partial class DestinationSearchView : ContentView
{
    // Expose events for the LobbyPage to listen to!
    public event EventHandler<PlaceSelectedEventArgs> PreviewRequested;
    public event EventHandler<PlaceSelectedEventArgs> Confirmed;
    public event EventHandler Cleared;

    private static readonly HttpClient _httpClient = new();
    private readonly string _googleApiKey = "AIzaSyA8t2qkOm6A9K8ZM-uYyJp5gnLVZCEHWzk"; // Moving this out of LobbyPage!
    private CancellationTokenSource _debounceCts;

    private bool _isInternalUpdate = false;
    private Location _pendingLocation;

    public DestinationSearchView()
    {
        InitializeComponent();
    }

    // A public method so LobbyPage can lock/unlock the UI during state changes
    public void SetState(bool isVisible, bool isReadOnly, bool showBanner, bool showConfirm)
    {
            this.IsVisible = isVisible;
            DestinationSearchBar.IsReadOnly = isReadOnly;
            InstructionBanner.IsVisible = showBanner;
            ConfirmDestButton.IsVisible = showConfirm;

            if (!isVisible) SuggestionsFrame.IsVisible = false;
    }

    // A public method so LobbyPage can push Native POI Map Clicks into the search bar
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

    // A public method to reset the bar when canceling a trip
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

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInternalUpdate) return;
        if (e.OldTextValue == e.NewTextValue) return;

        string query = e.NewTextValue;

        if (string.IsNullOrWhiteSpace(query))
        {
            Cleared?.Invoke(this, EventArgs.Empty); // Tell LobbyPage to wipe the map!
            return;
        }

        InstructionBanner.IsVisible = false;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        try
        {
            await Task.Delay(500, token); // Proper Debounce!
            if (token.IsCancellationRequested) return;

            var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
            request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

            var reqBody = new { input = query }; // Anonymous object works perfectly for serialization
            request.Content = new StringContent(JsonSerializer.Serialize(reqBody), System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<AutocompleteResponse>(responseBody); // Assuming you have this model

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
        // Using dynamic/reflection because we used an anonymous type above
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
            var details = JsonSerializer.Deserialize<PlaceDetailsResponse>(responseBody); // Assuming model exists

            if (details?.Location != null)
            {
                _pendingLocation = new Location(details.Location.Latitude, details.Location.Longitude);
                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;

                // Tell LobbyPage to preview the route!
                PreviewRequested?.Invoke(this, new PlaceSelectedEventArgs { Location = _pendingLocation, Name = desc });
            }
        }
        catch (Exception) { /* Handle error */ }

        SuggestionsListView.SelectedItem = null;
    }

    private async void OnSearchPressed(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DestinationSearchBar.Text)) return;
        try
        {
            var locations = await Geocoding.Default.GetLocationsAsync(DestinationSearchBar.Text);
            var location = locations?.FirstOrDefault();
            if (location != null)
            {
                _pendingLocation = location;
                ConfirmDestButton.IsEnabled = true;
                ConfirmDestButton.BackgroundColor = Colors.MediumSeaGreen;

                // Tell LobbyPage to preview the route!
                PreviewRequested?.Invoke(this, new PlaceSelectedEventArgs { Location = _pendingLocation, Name = DestinationSearchBar.Text });
            }
        }
        catch (Exception) { }
    }

    private void OnConfirmDestinationClicked(object sender, EventArgs e)
    {
        if (_pendingLocation == null || string.IsNullOrWhiteSpace(DestinationSearchBar.Text)) return;

        // Tell LobbyPage to lock it in!
        Confirmed?.Invoke(this, new PlaceSelectedEventArgs { Location = _pendingLocation, Name = DestinationSearchBar.Text });
        ConfirmDestButton.IsVisible = false;
    }
}