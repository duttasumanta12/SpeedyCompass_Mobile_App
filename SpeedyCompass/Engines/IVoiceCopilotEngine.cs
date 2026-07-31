using Microsoft.Maui.Media;
using SpeedyCompass.Models;

namespace SpeedyCompass.Engines;

public interface IVoiceCopilotEngine
{
    void ProcessTurnByTurn(Location currentGPS, List<RouteStep> activeSteps);
    void Speak(string message);
}
