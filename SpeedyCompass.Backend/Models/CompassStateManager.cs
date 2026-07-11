using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SpeedyCompass.Backend.Hubs;

// The Singleton Service
public class CompassStateManager
{
    // MongoDB Collections
    public IMongoCollection<UserAccount> UserAccounts { get; }
    public IMongoCollection<GroupSession> ActiveGroups { get; }

    // Fast RAM state for WebSockets (0ms latency)
    public ConcurrentDictionary<string, RiderSession> ConnectedRiders { get; } = new();
    public ConcurrentDictionary<string, string> ActiveSpeakers { get; } = new(); // Tracks who holds the PTT Mic

    public CompassStateManager(IConfiguration config)
    {
        try
        {
            // Fallback to local MongoDB if the Cosmos connection string isn't found
            var connectionString = config.GetConnectionString("CosmosMongoDb") ?? "mongodb://localhost:27017";

            var client = new MongoClient(connectionString);
            var database = client.GetDatabase("SpeedyCompassDB");

            UserAccounts = database.GetCollection<UserAccount>("Users");
            ActiveGroups = database.GetCollection<GroupSession>("Groups");
        } 
        catch (Exception ex)
        {
            // Log the exception (you can replace this with your preferred logging mechanism)
            Console.WriteLine($"Error initializing CompassStateManager: {ex.Message}");
            throw; // Rethrow the exception to ensure the application is aware of the failure
        }
    }
}