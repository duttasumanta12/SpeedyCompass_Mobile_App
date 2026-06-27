using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{
    public class AutocompleteResponse
    {
        [JsonPropertyName("suggestions")]
        public List<Suggestion> Suggestions { get; set; } = new();
    }
}
