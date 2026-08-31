using SpeedyCompass.Engines;

namespace SpeedyCompass.Services.Coordinators;

public sealed class PttCoordinator : IDisposable
{
    private readonly SignalRService _signalR;
    private readonly IVoiceCopilotEngine _voice;
    private readonly Func<string> _groupName;
    private readonly Func<string> _myName;
    private readonly Func<int> _onlineCount;
    private readonly Action<Action> _ui;

    private readonly Action _showMicOpen;
    private readonly Action<string> _showListening;
    private readonly Action<int> _updateCountdown;
    private readonly Action _showMaxTimeReached;
    private readonly Action _hideOverlay;
    private readonly Action<float, float> _updateSpectrum;
    private readonly Func<bool> _overlayVisible;

    private CancellationTokenSource? _pttCts;

    public string CurrentSpeaker { get; private set; } = string.Empty;

    public PttCoordinator(
        SignalRService signalR,
        IVoiceCopilotEngine voice,
        Func<string> groupName,
        Func<string> myName,
        Func<int> onlineCount,
        Action<Action> ui,
        Action showMicOpen,
        Action<string> showListening,
        Action<int> updateCountdown,
        Action showMaxTimeReached,
        Action hideOverlay,
        Action<float, float> updateSpectrum,
        Func<bool> overlayVisible)
    {
        _signalR = signalR;
        _voice = voice;
        _groupName = groupName;
        _myName = myName;
        _onlineCount = onlineCount;
        _ui = ui;
        _showMicOpen = showMicOpen;
        _showListening = showListening;
        _updateCountdown = updateCountdown;
        _showMaxTimeReached = showMaxTimeReached;
        _hideOverlay = hideOverlay;
        _updateSpectrum = updateSpectrum;
        _overlayVisible = overlayVisible;
    }

    public void HandleLocked(string speakerName)
    {
        CurrentSpeaker = speakerName;
        _ui(() =>
        {
            if (speakerName == _myName())
            {
                _showMicOpen();
                _pttCts?.Cancel();
                _pttCts = new CancellationTokenSource();
                _ = RunTimeoutAsync(_pttCts.Token);
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200));
                //_voice.Speak("You can now speak.");
            }
            else
            {
                _pttCts?.Cancel();
                _showListening(speakerName);
            }
        });
    }

    public void HandleDenied()
    {
        _ui(() =>
        {
            _voice.Speak("Channel busy.");
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
        });
    }

    public void HandleReleased()
    {
        CurrentSpeaker = string.Empty;
        _ui(() =>
        {
            _pttCts?.Cancel();
            _hideOverlay();
        });
    }

    public async Task HandleHardwarePressedAsync(bool isEnabled)
    {
        if (!isEnabled) return;
        if (_onlineCount() <= 1) return;
        if (CurrentSpeaker == _myName()) return;

        await _signalR.RequestPtt(_groupName(), _myName());
    }

    public async Task HandleHardwareReleasedAsync()
    {
        if (CurrentSpeaker == _myName())
        {
            await _signalR.ReleasePtt(_groupName(), _myName());
        }
    }

    public async Task HandleOverlayCloseRequestedAsync()
    {
        _pttCts?.Cancel();

        if (CurrentSpeaker == _myName())
        {
            await _signalR.ReleasePtt(_groupName(), _myName());
        }
        else
        {
            _ui(_hideOverlay);
        }
    }

    public void HandleAudioLevels(float outgoingLevel, float incomingLevel)
    {
        _ui(() =>
        {
            if (!_overlayVisible()) return;
            _updateSpectrum(outgoingLevel, incomingLevel);
        });
    }

    private async Task RunTimeoutAsync(CancellationToken token)
    {
        var remaining = 30;
        try
        {
            while (remaining > 0 && !token.IsCancellationRequested)
            {
                var snap = remaining;
                _ui(() => _updateCountdown(snap));
                await Task.Delay(1000, token);
                remaining--;
            }

            if (remaining <= 0 && !token.IsCancellationRequested)
            {
                _ui(() =>
                {
                    _showMaxTimeReached();
                    _voice.Speak("Microphone closed.");
                });

                if (CurrentSpeaker == _myName())
                {
                    await _signalR.ReleasePtt(_groupName(), _myName());
                }
            }
        }
        catch (TaskCanceledException) { }
    }

    public void Dispose()
    {
        _pttCts?.Cancel();
        _pttCts?.Dispose();
    }
}