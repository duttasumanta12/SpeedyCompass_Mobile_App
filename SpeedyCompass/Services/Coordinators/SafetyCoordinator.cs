using SpeedyCompass.Engines;
using SpeedyCompass.Shared;

namespace SpeedyCompass.Services.Coordinators;

public sealed class SafetyCoordinator : IDisposable
{
    // --- CRASH CONSTANTS ---
    // 1G is standard gravity. Handlebar potholes often hit 4-5G. 
    // 6.0G+ usually indicates a severe vehicular impact or dropping the bike.
    private const double CrashThresholdGForce = 6.0;
    private const int CrashCooldownMinutes = 5;

    private readonly SignalRService _signalR;
    private readonly IVoiceCopilotEngine _voice;
    private readonly Func<string> _groupName;
    private readonly Func<string> _myName;
    private readonly Action<Action> _ui;
    private readonly Action _showCrashAlert;
    private readonly Action<int> _updateCrashCountdown;
    private readonly Action _hideCrashAlert;

    private CancellationTokenSource? _crashCts;
    private DateTime _lastCrashEvent = DateTime.MinValue;

    // NEW: Thread safety lock to prevent multi-firing during a chaotic tumble
    private readonly object _crashLock = new();

    public SafetyCoordinator(
        SignalRService signalR,
        IVoiceCopilotEngine voice,
        Func<string> groupName,
        Func<string> myName,
        Action<Action> ui,
        Action showCrashAlert,
        Action<int> updateCrashCountdown,
        Action hideCrashAlert)
    {
        _signalR = signalR;
        _voice = voice;
        _groupName = groupName;
        _myName = myName;
        _ui = ui;
        _showCrashAlert = showCrashAlert;
        _updateCrashCountdown = updateCrashCountdown;
        _hideCrashAlert = hideCrashAlert;
    }

    public void ToggleCrashDetection(bool enable)
    {
        try
        {
            if (enable && Accelerometer.Default.IsSupported && !Accelerometer.Default.IsMonitoring)
            {
                Accelerometer.Default.ReadingChanged += OnAccelerometerReadingChanged;

                // THE FIX: Upgrade from UI (~60ms) to Game (~20ms) to catch microsecond impacts
                Accelerometer.Default.Start(SensorSpeed.Game);
            }
            else if (!enable && Accelerometer.Default.IsMonitoring)
            {
                Accelerometer.Default.ReadingChanged -= OnAccelerometerReadingChanged;
                Accelerometer.Default.Stop();
            }
        }
        catch { }
    }

    public void TriggerCrashProtocol()
    {
        _ui(_showCrashAlert);
        _voice.Speak("Collision detected. Are you okay? An emergency alert will be sent to the group in 10 seconds.");

        _crashCts?.Cancel();
        _crashCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            try
            {
                for (var i = 10; i > 0; i--)
                {
                    var snap = i;
                    _ui(() => _updateCrashCountdown(snap));
                    await Task.Delay(1000, _crashCts.Token);
                }

                _ui(_hideCrashAlert);
                _voice.Speak("No response. Sending automatic emergency alert to the group.");
                _signalR.SendGroupAlert(_groupName(), "Emergency", _myName()).SafeFireAndForget();
            }
            catch (OperationCanceledException) { }
        }, _crashCts.Token).SafeFireAndForget();
    }

    public void CancelCrashProtocol()
    {
        _crashCts?.Cancel();
        _ui(_hideCrashAlert);
        _voice.Speak("Emergency cancelled. Glad you are okay.");
    }

    public void ConfirmEmergency()
    {
        _crashCts?.Cancel();
        _ui(_hideCrashAlert);
        _voice.Speak("Manual emergency triggered.");
        _signalR.SendGroupAlert(_groupName(), "Emergency", _myName()).SafeFireAndForget();
    }

    private void OnAccelerometerReadingChanged(object? sender, AccelerometerChangedEventArgs e)
    {
        // THE FIX: Math.Pow is too heavy for a 50Hz hardware loop. Direct multiplication is vastly faster.
        double gForce = Math.Sqrt(
            (e.Reading.Acceleration.X * e.Reading.Acceleration.X) +
            (e.Reading.Acceleration.Y * e.Reading.Acceleration.Y) +
            (e.Reading.Acceleration.Z * e.Reading.Acceleration.Z));

        if (gForce > CrashThresholdGForce)
        {
            // THE FIX: Lock the thread so a multi-tumble crash doesn't trigger 5 alarms at once
            lock (_crashLock)
            {
                if ((DateTime.Now - _lastCrashEvent).TotalMinutes > CrashCooldownMinutes)
                {
                    _lastCrashEvent = DateTime.Now;

                    // Push execution to the background to instantly free up the OS hardware sensor thread
                    Task.Run(() => TriggerCrashProtocol()).SafeFireAndForget();
                }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Accelerometer.Default.ReadingChanged -= OnAccelerometerReadingChanged;
            if (Accelerometer.Default.IsMonitoring)
            {
                Accelerometer.Default.Stop();
            }
        }
        catch { }

        _crashCts?.Cancel();
        _crashCts?.Dispose();
    }
}