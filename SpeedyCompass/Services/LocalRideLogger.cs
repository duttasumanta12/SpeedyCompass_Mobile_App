using Microsoft.Maui.Storage;
using SpeedyCompass.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpeedyCompass.Services;

/// <summary>
/// Handles saving and retrieving ride history from the device's local file system.
/// </summary>
public static class LocalRideLogger
{
    // Store the JSON file securely in the app's sandboxed AppDataDirectory
    private static readonly string FilePath = Path.Combine(FileSystem.AppDataDirectory, "ride_history.json");

    /// <summary>
    /// Appends a new ride summary to the local JSON history file.
    /// </summary>
    public static async Task SaveRideAsync(RideSummary newRide)
    {
        try
        {
            // 1. Fetch existing history
            var history = await GetRideHistoryAsync();

            // 2. Append the new ride
            history.Add(newRide);

            // 3. Serialize back to JSON and overwrite the file
            string jsonString = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(FilePath, jsonString);

            System.Diagnostics.Debug.WriteLine($"[RideLogger] Saved successfully to {FilePath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RideLogger] Error saving ride: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves the list of all past rides from the local JSON file.
    /// </summary>
    public static async Task<List<RideSummary>> GetRideHistoryAsync()
    {
        try
        {
            // If the file doesn't exist yet (first time using the app), return an empty list
            if (!File.Exists(FilePath))
                return new List<RideSummary>();

            // Read the JSON and deserialize it
            string jsonString = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<List<RideSummary>>(jsonString) ?? new List<RideSummary>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RideLogger] Error reading history: {ex.Message}");
            return new List<RideSummary>();
        }
    }
}