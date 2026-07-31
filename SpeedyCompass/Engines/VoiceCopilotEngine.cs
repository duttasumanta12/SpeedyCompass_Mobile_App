using SpeedyCompass.Models;

namespace SpeedyCompass.Engines;

public class VoiceCopilotEngine : IVoiceCopilotEngine
{
    public void Speak(string message)
    {
        _ = TextToSpeech.Default.SpeakAsync(message);
    }

    public void ProcessTurnByTurn(Location currentGPS, List<RouteStep> activeSteps)
    {
        if (!Preferences.Default.Get("Map_VoiceNav", true)) return;
        if (activeSteps == null || !activeSteps.Any()) return;

        // 1. AUTO-SKIP PASSED STEPS
        for (int i = 0; i < activeSteps.Count; i++)
        {
            var step = activeSteps[i];
            if (step.VoiceAlertPlayed) continue;

            double distToThis = Location.CalculateDistance(currentGPS, step.TurnLocation, DistanceUnits.Kilometers) * 1000;
            if (distToThis < 25) { step.VoiceAlertPlayed = true; continue; }

            if (i + 1 < activeSteps.Count)
            {
                double distToNext = Location.CalculateDistance(currentGPS, activeSteps[i + 1].TurnLocation, DistanceUnits.Kilometers) * 1000;
                if (distToNext < distToThis) { step.VoiceAlertPlayed = true; continue; }
            }
            break;
        }

        // 2. IDENTIFY ACTIVE TURN
        var nextStep = activeSteps.FirstOrDefault(s => !s.VoiceAlertPlayed);
        if (nextStep == null) return;

        double distanceMeters = Location.CalculateDistance(currentGPS, nextStep.TurnLocation, DistanceUnits.Kilometers) * 1000;

        // 3. DYNAMIC TRIGGER MATH
        double speedKmh = (currentGPS.Speed ?? 11.11) * 3.6;
        double dynamicTriggerDist = Math.Clamp((speedKmh / 3.6) * 10, 100, 350);

        var previousStep = activeSteps.LastOrDefault(s => s.VoiceAlertPlayed);
        if (previousStep != null)
        {
            double distBetween = Location.CalculateDistance(previousStep.TurnLocation, nextStep.TurnLocation, DistanceUnits.Kilometers) * 1000;
            if (distBetween < 250) dynamicTriggerDist = Math.Clamp(distBetween * 0.6, 30, dynamicTriggerDist);
        }

        // 4. SPEAK
        if (distanceMeters <= dynamicTriggerDist)
        {
            nextStep.VoiceAlertPlayed = true;
            int spokenDistance = Math.Max(50, (int)(Math.Round(distanceMeters / 50.0) * 50));
            string cleanInstruction = CleanVoiceInstruction(nextStep.Instruction);
            Speak($"In {spokenDistance} meters, {cleanInstruction}");
        }
    }

    private string CleanVoiceInstruction(string rawInstruction)
    {
        if (string.IsNullOrWhiteSpace(rawInstruction)) return "";

        // 1. Remove all HTML tags (e.g. <b>, <div>, <wbr>)
        string clean = System.Text.RegularExpressions.Regex.Replace(rawInstruction, "<.*?>", " ");

        // 2. Decode HTML entities (e.g. &amp; becomes &, &nbsp; becomes a space)
        clean = System.Net.WebUtility.HtmlDecode(clean);

        // 3. Expand common road abbreviations so the voice doesn't stutter or mispronounce them
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\bNH\b", "National Highway", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\bSH\b", "State Highway", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\bRd\b", "Road", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\bSt\b", "Street", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\bHwy\b", "Highway", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 4. INJECT PACING: Native TTS engines pause whenever they hit a comma.
        // We force a pause between the action and the road name, and before destinations.
        clean = clean.Replace(" onto ", ", onto, ");
        clean = clean.Replace(" towards ", ", towards, ");
        clean = clean.Replace(" to stay on ", ", to stay on, ");
        clean = clean.Replace(" and ", ", and, ");

        // 5. Clean up any weird double spaces created by the replacements
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();

        return clean;
    }
}