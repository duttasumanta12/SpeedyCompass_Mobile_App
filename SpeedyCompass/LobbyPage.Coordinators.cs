using SpeedyCompass.Services.Coordinators;

namespace SpeedyCompass;

public partial class LobbyPage
{
    private PttCoordinator? _pttCoordinator;
    private SafetyCoordinator? _safetyCoordinator;

    private void InitializeCoordinators()
    {
        InitializeRouteCoordinator();

        _safetyCoordinator = new SafetyCoordinator(
            _signalRService,
            _voiceEngine,
            () => GroupNameLabel.Text,
            () => _myName,
            action => MainThread.BeginInvokeOnMainThread(action),
            () => SensoryAlertOverlay.ShowCrashAlert(),
            i => SensoryAlertOverlay.UpdateCrashCountdown(i),
            () => SensoryAlertOverlay.HideAlert());

        _pttCoordinator = new PttCoordinator(
            _signalRService,
            _voiceEngine,
            () => GroupNameLabel.Text,
            () => _myName,
            () => Riders.Count(r => r.IsOnline),
            action => MainThread.BeginInvokeOnMainThread(action),
            () => PttOverlay.ShowMicOpen(),
            name => PttOverlay.ShowListening(name),
            seconds => PttOverlay.UpdateCountdown(seconds),
            () => PttOverlay.ShowMaximumTimeReached(),
            () => PttOverlay.Hide(),
            (outgoing, incoming) => PttOverlay.UpdateSpectrum(outgoing, incoming),
            () => PttOverlay.IsVisible);
    }

    private void DisposeCoordinators()
    {
        _pttCoordinator?.Dispose();
        _pttCoordinator = null;

        _safetyCoordinator?.Dispose();
        _safetyCoordinator = null;
    }

    // wrappers keep existing handler signatures unchanged
    private void OnPttLocked(string speakerName) => _pttCoordinator?.HandleLocked(speakerName);
    private void OnPttDenied(string activeSpeaker) => _pttCoordinator?.HandleDenied();
    private void OnPttReleased() => _pttCoordinator?.HandleReleased();
    private async void OnHardwarePttPressed(object sender, EventArgs e) => await _pttCoordinator!.HandleHardwarePressedAsync(_isPttEnabled);
    private async void OnHardwarePttReleased(object sender, EventArgs e) => await _pttCoordinator!.HandleHardwareReleasedAsync();
    private async void OnPttOverlayCloseRequested(object sender, EventArgs e) => await _pttCoordinator!.HandleOverlayCloseRequestedAsync();
    private void OnPttAudioLevelsUpdated(float outgoingLevel, float incomingLevel) => _pttCoordinator?.HandleAudioLevels(outgoingLevel, incomingLevel);

    private void ToggleCrashDetection(bool enable) => _safetyCoordinator?.ToggleCrashDetection(enable);
    private void TriggerCrashProtocol() => _safetyCoordinator?.TriggerCrashProtocol();
    private void OnCrashCancelledClicked(object sender, EventArgs e) => _safetyCoordinator?.CancelCrashProtocol();
    private void OnCrashEmergencyClicked(object sender, EventArgs e) => _safetyCoordinator?.ConfirmEmergency();
}