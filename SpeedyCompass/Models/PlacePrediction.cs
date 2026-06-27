using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace SpeedyCompass.Models
{

    public class PlacePrediction
    {
        [JsonPropertyName("placeId")]
        public string PlaceId { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public PlaceText Text { get; set; }
    }
}
