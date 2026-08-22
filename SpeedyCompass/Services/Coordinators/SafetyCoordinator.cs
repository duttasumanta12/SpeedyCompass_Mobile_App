using SpeedyCompass.Engines;
using SpeedyCompass.Shared;

namespace SpeedyCompass.Services.Coordinators;

public sealed class SafetyCoordinator : IDisposable
{
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
                Accelerometer.Default.Start(SensorSpeed.UI);
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
        double gForce = Math.Sqrt(
            Math.Pow(e.Reading.Acceleration.X, 2) +
            Math.Pow(e.Reading.Acceleration.Y, 2) +
            Math.Pow(e.Reading.Acceleration.Z, 2));

        if (gForce > 4.5 && (DateTime.Now - _lastCrashEvent).TotalMinutes > 5)
        {
            _lastCrashEvent = DateTime.Now;
            TriggerCrashProtocol();
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