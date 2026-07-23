using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SpeedyCompass.Backend.Models
{
    public class GroupMember
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        public string GroupName { get; set; }
        public string GoogleId { get; set; }
        public string Name { get; set; }
        public bool IsAdmin { get; set; }
        public string Role { get; set; } // "Admin", "Lead", "Tail", "Marshal", "Rider"
        public bool IsOnline { get; set; }
        public string ConnectionId { get; set; }

        // Live Telemetry stored directly on the member record
        public double LastLat { get; set; }
        public double LastLng { get; set; }
        public double Heading { get; set; }
        public DateTime LastUpdate { get; set; }

        public DateTime JoinedAt { get; set; }
    }
}
