using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using SpeedyCompass.Backend.Models;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SpeedyCompass.Backend.Hubs;

// The Singleton Service
public class CompassStateManager
{
    // MongoDB Collections
    public IMongoCollection<UserAccount> UserAccounts { get; }
    public IMongoCollection<GroupSession> ActiveGroups { get; }
    public IMongoCollection<GroupMember> GroupMembers { get; }

    public ConcurrentDictionary<string, string> ActiveSpeakers { get; } = new(); // Tracks who holds the PTT Mic
    // 2. Fast In-Memory Cache (Protects the DB from Spam)
    private readonly IMemoryCache _cache;

    public CompassStateManager(IConfiguration config, IMemoryCache cache)
    {
        _cache = cache;

        try
        {
            var connectionString = config.GetConnectionString("CosmosMongoDb") ?? "mongodb://localhost:27017";
            var client = new MongoClient(connectionString);
            var database = client.GetDatabase("SpeedyCompassDB");

            UserAccounts = database.GetCollection<UserAccount>("Users");
            ActiveGroups = database.GetCollection<GroupSession>("Groups");

            // Register the new Members table
            GroupMembers = database.GetCollection<GroupMember>("GroupMembers");

            // --- CREATE INDEXES FOR LIGHTNING FAST QUERIES ---
            var groupIndex = Builders<GroupMember>.IndexKeys.Ascending(m => m.GroupName);
            GroupMembers.Indexes.CreateOne(new CreateIndexModel<GroupMember>(groupIndex));

            var userIndex = Builders<GroupMember>.IndexKeys.Ascending(m => m.GoogleId);
            GroupMembers.Indexes.CreateOne(new CreateIndexModel<GroupMember>(userIndex));
        }
        catch (Exception ex)
        {
            // Log the exception (you can replace this with your preferred logging mechanism)
            Console.WriteLine($"Error initializing CompassStateManager: {ex.Message}");
            throw; // Rethrow the exception to ensure the application is aware of the failure
        }
    }
    // ====================================================================
    // --- THE MEMORY CACHE SAFETY NET ---
    // Reads from RAM first. If missing, reads from DB and caches it for 3 seconds.
    // This ensures that when 20 riders update their location at the exact same second,
    // we only query the DB ONCE for the group settings!
    // ====================================================================
    public async Task<GroupSession> GetGroupCachedAsync(string groupName)
    {
        return await _cache.GetOrCreateAsync($"group_{groupName}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3);
            return await ActiveGroups.Find(g => g.GroupName == groupName).FirstOrDefaultAsync();
        });
    }
}