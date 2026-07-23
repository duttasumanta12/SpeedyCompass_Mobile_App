using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace SpeedyCompass.Backend.Models;

public class SchemaMigration
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }

    // The unique name of the migration (e.g., "001_ExtractGroupMembers")
    public string MigrationName { get; set; }

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}