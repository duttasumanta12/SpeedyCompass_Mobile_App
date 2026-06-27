using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{
    public class Suggestion
    {
        [JsonPropertyName("placePrediction")]
        public PlacePrediction PlacePrediction { get; set; }
    }
}
