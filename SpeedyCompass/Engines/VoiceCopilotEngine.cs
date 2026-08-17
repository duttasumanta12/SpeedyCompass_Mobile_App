using Microsoft.Extensions.Logging;
using SpeedyCompass.Models;
using SpeedyCompass.Services;
using SpeedyCompass.Shared.Models;
using System.Text.RegularExpressions;

namespace SpeedyCompass.Engines;

public class VoiceCopilotEngine : IVoiceCopilotEngine
{
    private readonly HardwareButtonService _hwButton;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly ILogger<VoiceCopilotEngine> _logger;
    private readonly object _speechLock = new();

    private DateTime _lastSpokenAtUtc = DateTime.MinValue;
    private string _lastSpokenText = string.Empty;

    private const int MinSpeechGapMs = 1100;
    private const int DuplicateCooldownMs = 6000;

    public VoiceCopilotEngine(HardwareButtonService hwButton, ILogger<VoiceCopilotEngine> logger)
    {
        _hwButton = hwButton;
        _logger = logger;
    }

    public void Speak(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var normalized = NormalizeSpeech(message);
        if (!ShouldSpeak(normalized)) return;

        _ = SpeakInternalAsync(normalized);
    }

    public void ProcessTurnByTurn(Location currentGPS, List<RouteStep> activeSteps)
    {
        if (!Preferences.Default.Get("Map_VoiceNav", true)) return;
        if (currentGPS == null || activeSteps == null || activeSteps.Count == 0) return;

        // 1) Cleanup passed steps
        for (int i = 0; i < activeSteps.Count; i++)
        {
            var step = activeSteps[i];
            if (step.StepCompleted) continue;

            double distToThis = DistanceMeters(currentGPS, step.TurnLocation);

            if (distToThis <= 25)
            {
                step.StepCompleted = true;
                continue;
            }

            if (i + 1 < activeSteps.Count)
            {
                double distToNext = DistanceMeters(currentGPS, activeSteps[i + 1].TurnLocation);
                if (distToNext < distToThis)
                {
                    step.StepCompleted = true;
                    continue;
                }
            }

            break;
        }

        var nextStep = activeSteps.FirstOrDefault(s => !s.StepCompleted && !s.ShortRangeAlertPlayed);
        if (nextStep == null) return;

        double distanceMeters = DistanceMeters(currentGPS, nextStep.TurnLocation);
        double speedKmh = (currentGPS.Speed ?? 0) * 3.6;
        string instruction = CleanVoiceInstruction(nextStep.Instruction);

        // 2) Far context alert (Google Maps-like "continue, then ...")
        if (!nextStep.LongRangeAlertPlayed && distanceMeters is > 1200 and <= 4000)
        {
            nextStep.LongRangeAlertPlayed = true;
            Speak($"Continue for {FormatDistance(distanceMeters)}, then {instruction}");
            return;
        }

        // 3) Prepare alert
        // Reusing VoiceAlertPlayed as the mid-range prep flag.
        double prepTrigger = Math.Clamp((speedKmh / 3.6) * 20, 250, 900);
        if (!nextStep.VoiceAlertPlayed && distanceMeters <= prepTrigger && distanceMeters > 120)
        {
            nextStep.VoiceAlertPlayed = true;
            Speak($"Prepare to {instruction} in {FormatDistance(distanceMeters)}");
            return;
        }

        // 4) Final alert ("In X meters ...")
        double finalTrigger = Math.Clamp((speedKmh / 3.6) * 10, 80, 300);

        var lastCompleted = activeSteps.LastOrDefault(s => s.StepCompleted);
        if (lastCompleted != null)
        {
            double distBetween = DistanceMeters(lastCompleted.TurnLocation, nextStep.TurnLocation);
            if (distBetween < 250)
            {
                finalTrigger = Math.Clamp(distBetween * 0.6, 30, finalTrigger);
            }
        }

        if (distanceMeters <= finalTrigger)
        {
            nextStep.ShortRangeAlertPlayed = true;
            nextStep.LongRangeAlertPlayed = true;

            int spokenMeters = Math.Max(30, (int)(Math.Round(distanceMeters / 10.0) * 10));
            if (spokenMeters <= 40)
                Speak($"Now, {instruction}");
            else
                Speak($"In {spokenMeters} meters, {instruction}");
        }
    }

    private async Task SpeakInternalAsync(string message)
    {
        bool gateAcquired = false;
        try
        {
            gateAcquired = await _speechGate.WaitAsync(TimeSpan.FromSeconds(2));
            if (!gateAcquired)
            {
                _logger.LogWarning("TTS gate timeout. Dropping speech: {Message}", message);
                return;
            }

            int waitMs = 0;
            lock (_speechLock)
            {
                var elapsed = (DateTime.UtcNow - _lastSpokenAtUtc).TotalMilliseconds;
                if (elapsed < MinSpeechGapMs)
                    waitMs = (int)(MinSpeechGapMs - elapsed);
            }

            if (waitMs > 0) await Task.Delay(waitMs);

            var speakTask = TextToSpeech.Default.SpeakAsync(message, new SpeechOptions
            {
                Pitch = 1.0f,
                Volume = 1.0f
            });

            // hard timeout so queue doesn't freeze forever on emulator quirks
            var completed = await Task.WhenAny(speakTask, Task.Delay(TimeSpan.FromSeconds(8)));
            if (completed != speakTask)
            {
                _logger.LogWarning("TTS timeout. Message skipped: {Message}", message);
                return;
            }

            // observe exceptions from speakTask
            await speakTask;

            lock (_speechLock)
            {
                _lastSpokenAtUtc = DateTime.UtcNow;
                _lastSpokenText = message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS failed for message: {Message}", message);
        }
        finally
        {
            if (gateAcquired)
                _speechGate.Release();
        }
    }

    private bool ShouldSpeak(string normalized)
    {
        lock (_speechLock)
        {
            bool isDuplicate = string.Equals(_lastSpokenText, normalized, StringComparison.OrdinalIgnoreCase);
            bool stillCoolingDown = (DateTime.UtcNow - _lastSpokenAtUtc).TotalMilliseconds < DuplicateCooldownMs;

            if (isDuplicate && stillCoolingDown) return false;
            return true;
        }
    }

    private static string NormalizeSpeech(string text)
    {
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static double DistanceMeters(Location a, Location b)
    {
        return Location.CalculateDistance(a, b, DistanceUnits.Kilometers) * 1000.0;
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 1000)
        {
            double km = Math.Round(meters / 1000.0, 1);
            return $"{km:0.#} kilometers";
        }

        int rounded = Math.Max(50, (int)(Math.Round(meters / 10.0) * 10));
        return $"{rounded} meters";
    }

    private string CleanVoiceInstruction(string rawInstruction)
    {
        if (string.IsNullOrWhiteSpace(rawInstruction)) return string.Empty;

        string clean = rawInstruction.Replace("div", "span");
        clean = Regex.Replace(clean, "<.*?>", " ");
        clean = System.Net.WebUtility.HtmlDecode(clean);

        clean = Regex.Replace(clean, @"\bNH\b", "National Highway", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bSH\b", "State Highway", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bRd\b", "Road", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bSt\b", "Street", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bHwy\b", "Highway", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bAve\b", "Avenue", RegexOptions.IgnoreCase);

        clean = clean.Replace(" onto ", ", onto, ");
        clean = clean.Replace(" towards ", ", towards, ");
        clean = clean.Replace(" and ", ", and, ");

        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean;
    }
}