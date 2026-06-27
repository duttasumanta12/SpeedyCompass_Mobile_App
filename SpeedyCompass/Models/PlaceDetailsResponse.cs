using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{
    public class PlaceDetailsResponse
    {
        [JsonPropertyName("location")]
        public PlaceLocation Location { get; set; }
    }
}
