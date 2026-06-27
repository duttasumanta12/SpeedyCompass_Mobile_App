using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{
    public class PlaceText
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }
}
