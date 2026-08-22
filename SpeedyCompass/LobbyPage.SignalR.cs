using SpeedyCompass.Services;

namespace SpeedyCompass;

public partial class LobbyPage
{
    private LobbySignalRBinding? _signalRBinding;

    private void InitializeSignalRBindings()
    {
        _signalRBinding = new LobbySignalRBinding(
            signalR: _signalRService,
            onConnectionStatusChanged: OnConnectionStatusChanged,
            onRosterUpdated: OnRosterUpdated,
            onNavigationStarted: OnNavigationStarted,
            onRiderLocationUpdated: OnRiderLocationUpdated,
            onNavigationCancelled: OnNavigationCancelled,
            onAlertReceived: OnAlertReceived,
            onDestinationSet: OnSignalRDestinationSetReceived,
            onUserJoined: OnUserJoined,
            onUserLeft: OnUserLeft,
            onGroupDeleted: OnGroupDeleted,
            onNavigationPaused: OnNavigationPaused,
            onNavigationResumed: OnNavigationResumed,
            onNavigationCompleted: OnNavigationCompleted,
            onLeadRouteUpdated: OnLeadRouteUpdated,
            onRouteDeviationAlert: OnRouteDeviationAlert,
            onMeetupPointSet: OnMeetupPointSet,
            onGroupSettingsUpdated: OnSettingsPushedFromServer,
            onVisibilityToggleReceived: OnVisibilityToggleReceived,
            onPttLocked: _isPttEnabled ? OnPttLocked : null,
            onPttDenied: _isPttEnabled ? OnPttDenied : null,
            onPttReleased: _isPttEnabled ? OnPttReleased : null);

        _signalRBinding.Attach();
    }

    private void DisposeSignalRBindings()
    {
        _signalRBinding?.Detach();
        _signalRBinding = null;
    }
}