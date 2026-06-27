using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{
    // 1. Models to deserialize the Google Places API (New) JSON responses
    public class AutocompleteRequest
    {
        [JsonPropertyName("input")]
        public string Input { get; set; } = string.Empty;
    }
}
