using SpeedyCompass.Models;

namespace SpeedyCompass.Services;

public sealed class NoOpPttMeshService : IPttMeshService
{
    public event Action<float, float>? AudioLevelsUpdated
    {
        add { }
        remove { }
    }

    public void InitializeSession(string groupName, string myGoogleId) { }

    public Task SyncMeshNetworkAsync(List<Rider> currentRoster) => Task.CompletedTask;
}