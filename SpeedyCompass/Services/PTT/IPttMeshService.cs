using SpeedyCompass.Models;

namespace SpeedyCompass.Services;

public interface IPttMeshService
{
    event Action<float, float>? AudioLevelsUpdated;
    void InitializeSession(string groupName, string myGoogleId);
    void StopSession();
    Task SyncMeshNetworkAsync(List<Rider> currentRoster);
}