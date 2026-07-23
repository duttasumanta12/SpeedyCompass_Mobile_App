using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using SpeedyCompass.Backend.Hubs;
using SpeedyCompass.Backend.Models;
using SpeedyCompass.Shared.Models;
using System;
using System.Threading.Tasks;

namespace SpeedyCompass.Backend.Services;

public class DatabaseMigrationService
{
    private readonly CompassStateManager _state;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<SchemaMigration> _migrations;

    public DatabaseMigrationService(CompassStateManager state, IConfiguration config)
    {
        _state = state;
        var connectionString = config.GetConnectionString("CosmosMongoDb") ?? "mongodb://localhost:27017";
        var client = new MongoClient(connectionString);
        _db = client.GetDatabase("SpeedyCompassDB");
        _migrations = _db.GetCollection<SchemaMigration>("SchemaMigrations");
    }

    public async Task ApplyMissingMigrationsAsync()
    {
        Console.WriteLine("Checking for pending database migrations...");

        // =================================================================
        // ADD NEW MIGRATIONS HERE IN THE FUTURE
        // =================================================================

        await ExecuteMigrationIfMissing("001_ExtractGroupMembersToNewTable", Migration_001_ExtractGroupMembers);

        // Example for your next release:
        // await ExecuteMigrationIfMissing("002_AddUserAvatars", Migration_002_AddUserAvatars);

        Console.WriteLine("Database is up to date!");
    }

    private async Task ExecuteMigrationIfMissing(string migrationName, Func<Task> migrationLogic)
    {
        var alreadyRun = await _migrations.Find(m => m.MigrationName == migrationName).AnyAsync();
        if (alreadyRun) return; // Skip if already applied

        Console.WriteLine($"Applying Migration: {migrationName}...");

        try
        {
            await migrationLogic();

            // Mark as completed so it never runs again
            await _migrations.InsertOneAsync(new SchemaMigration { MigrationName = migrationName });
            Console.WriteLine($"[SUCCESS] {migrationName} applied.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL ERROR] Migration {migrationName} failed: {ex.Message}");
            throw; // Stop the server from booting if a critical migration fails
        }
    }

    // =================================================================
    // MIGRATION SCRIPTS
    // =================================================================

    private async Task Migration_001_ExtractGroupMembers()
    {
        // Get raw access to BsonDocuments to read old schema fields that no longer exist in C#
        var groupsRawCol = _db.GetCollection<BsonDocument>("Groups");
        var allGroups = await groupsRawCol.Find(new BsonDocument()).ToListAsync();

        foreach (var groupDoc in allGroups)
        {
            string groupName = groupDoc["GroupName"].AsString;
            string adminId = groupDoc["AdminGoogleId"].AsString;

            // Look for the old ActiveRiders array
            if (groupDoc.Contains("ActiveRiders") && groupDoc["ActiveRiders"].IsBsonArray)
            {
                var oldRidersArray = groupDoc["ActiveRiders"].AsBsonArray;

                foreach (var riderBson in oldRidersArray)
                {
                    string googleId = riderBson.AsString;

                    var alreadyExists = await _state.GroupMembers.Find(m => m.GroupName == groupName && m.GoogleId == googleId).AnyAsync();
                    if (alreadyExists) continue;

                    var userAccount = await _state.UserAccounts.Find(u => u.GoogleId == googleId).FirstOrDefaultAsync();
                    if (userAccount != null)
                    {
                        var newMember = new GroupMember
                        {
                            GroupName = groupName,
                            GoogleId = googleId,
                            Name = userAccount.Username,
                            Role = (googleId == adminId) ? "Admin" : "Rider",
                            IsOnline = false,
                            ConnectionId = string.Empty,
                            JoinedAt = DateTime.UtcNow
                        };
                        await _state.GroupMembers.InsertOneAsync(newMember);
                    }
                }
            }

            // Scrub the old data column from the database
            var update = Builders<BsonDocument>.Update.Unset("ActiveRiders");
            await groupsRawCol.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", groupDoc["_id"]), update);
        }
    }
}