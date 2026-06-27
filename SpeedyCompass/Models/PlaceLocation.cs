using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{
    public class PlaceLocation
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }
}
