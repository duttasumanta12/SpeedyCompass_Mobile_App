using System.Text.Json.Serialization;

namespace SpeedyCompass.Models;

public class RoutesRequest
{
    [JsonPropertyName("origin")] public RouteWaypoint Origin { get; set; }
    [JsonPropertyName("destination")] public RouteWaypoint Destination { get; set; }
    [JsonPropertyName("intermediates")] public List<RouteWaypoint> Intermediates { get; set; }
    [JsonPropertyName("travelMode")] public string TravelMode { get; set; } = "DRIVE";
    // ==========================================
    // THE FIX: Force the API to return English
    // ==========================================
    [JsonPropertyName("languageCode")] public string LanguageCode { get; set; } = "en-US";
}
public class RouteWaypoint { [JsonPropertyName("location")] public RouteLocation Location { get; set; } }
public class RouteLocation { [JsonPropertyName("latLng")] public RouteLatLng LatLng { get; set; }
    [JsonPropertyName("heading")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Heading { get; set; }
}
public class RouteLatLng { [JsonPropertyName("latitude")] public double Latitude { get; set; } [JsonPropertyName("longitude")] public double Longitude { get; set; } }
public class RoutesResponse { [JsonPropertyName("routes")] public List<RouteData> Routes { get; set; } }
public class RouteData
{
    [JsonPropertyName("distanceMeters")] public int DistanceMeters { get; set; }
    [JsonPropertyName("duration")] public string Duration { get; set; }
    [JsonPropertyName("polyline")] public RoutePolyline Polyline { get; set; }
    // NEW: Add Legs to get the turn-by-turn steps
    [JsonPropertyName("legs")] public List<RouteLeg> Legs { get; set; }
    [JsonPropertyName("travelAdvisory")] public RouteTravelAdvisory TravelAdvisory { get; set; }
}
public class RouteLeg { [JsonPropertyName("steps")] public List<RouteStepApi> Steps { get; set; } }
public class RoutePolyline { [JsonPropertyName("encodedPolyline")] public string EncodedPolyline { get; set; } }

public class RouteTravelAdvisory
{
    [JsonPropertyName("speedReadingIntervals")]
    public List<RouteSpeedReadingInterval> SpeedReadingIntervals { get; set; } = new();
}

public class RouteSpeedReadingInterval
{
    [JsonPropertyName("startPolylinePointIndex")]
    public int StartPolylinePointIndex { get; set; }

    [JsonPropertyName("endPolylinePointIndex")]
    public int EndPolylinePointIndex { get; set; }

    [JsonPropertyName("speed")]
    public string Speed { get; set; } = "NORMAL";
}

public class NearbySearchRequest
{
    [JsonPropertyName("includedTypes")] public List<string> IncludedTypes { get; set; }
    [JsonPropertyName("maxResultCount")] public int MaxResultCount { get; set; }
    [JsonPropertyName("locationRestriction")] public LocationRestriction LocationRestriction { get; set; }
}
public class LocationRestriction { [JsonPropertyName("circle")] public SearchCircle Circle { get; set; } }
public class SearchCircle { [JsonPropertyName("center")] public RouteLatLng Center { get; set; } [JsonPropertyName("radius")] public double Radius { get; set; } }
public class NearbySearchResponse { [JsonPropertyName("places")] public List<PlaceResult> Places { get; set; } }
public class PlaceResult
{
    [JsonPropertyName("displayName")] public DisplayName DisplayName { get; set; }
    [JsonPropertyName("location")] public RouteLatLng Location { get; set; }
    [JsonPropertyName("rating")] public double Rating { get; set; }
}
public class RouteStepApi
{
    [JsonPropertyName("startLocation")] public RouteLocation StartLocation { get; set; }
    [JsonPropertyName("navigationInstruction")] public RouteNavigationInstruction NavigationInstruction { get; set; }
}
public class RouteNavigationInstruction { [JsonPropertyName("instructions")] public string Instructions { get; set; } }
public class DisplayName { [JsonPropertyName("text")] public string Text { get; set; } }
public class SpeedLimitsResponse
{
    [JsonPropertyName("speedLimits")]
    public List<SpeedLimitData> SpeedLimits { get; set; }
}

public class SpeedLimitData
{
    [JsonPropertyName("speedLimit")]
    public int SpeedLimit { get; set; }

    [JsonPropertyName("units")]
    public string Units { get; set; }
}
public class SearchTextRequest
{
    [JsonPropertyName("textQuery")] public string TextQuery { get; set; }
    [JsonPropertyName("searchAlongRouteParameters")] public SearchAlongRouteParameters SearchAlongRouteParameters { get; set; }
    [JsonPropertyName("languageCode")] public string LanguageCode { get; set; } = "en-US";
}

public class SearchAlongRouteParameters
{
    [JsonPropertyName("polyline")] public RoutePolyline Polyline { get; set; }
}

public class MapPinViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private Location _location;
    private string _speed;
    private string _username;
    private Color _pinColor;

    public bool IsDestination { get; set; } = false;

    public Location Location { get => _location; set { _location = value; OnPropertyChanged(); } }
    public string Speed { get => _speed; set { _speed = value; OnPropertyChanged(); } }
    public string Username { get => _username; set { _username = value; OnPropertyChanged(); } }
    public Color PinColor { get => _pinColor; set { _pinColor = value; OnPropertyChanged(); } }

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

public class MapPinTemplateSelector : DataTemplateSelector
{
    public DataTemplate RiderTemplate { get; set; }
    public DataTemplate DestinationTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is MapPinViewModel vm && vm.IsDestination) return DestinationTemplate;
        return RiderTemplate;
    }
}