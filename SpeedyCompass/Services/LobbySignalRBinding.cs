using SpeedyCompass.Models;
using SpeedyCompass.Shared.Models;

namespace SpeedyCompass.Services;

public sealed class LobbySignalRBinding : IDisposable
{
    private readonly SignalRService _signalR;

    private readonly Action<string, Color> _onConnectionStatusChanged;
    private readonly Action<List<Rider>> _onRosterUpdated;
    private readonly Action<double, double, string, bool> _onNavigationStarted;
    private readonly Action<string, double, double, double> _onRiderLocationUpdated;
    private readonly Action _onNavigationCancelled;
    private readonly Action<string, string> _onAlertReceived;
    private readonly Action<double, double, string> _onDestinationSet;
    private readonly Action<string> _onUserJoined;
    private readonly Action<string> _onUserLeft;
    private readonly Action _onGroupDeleted;
    private readonly Action<string, string> _onNavigationPaused;
    private readonly Action<string> _onNavigationResumed;
    private readonly Action<string> _onNavigationCompleted;
    private readonly Action<string> _onLeadRouteUpdated;
    private readonly Action<string> _onRouteDeviationAlert;
    private readonly Action<double, double> _onMeetupPointSet;
    private readonly Action<GroupSettingsDto> _onGroupSettingsUpdated;
    private readonly Action<string, bool> _onVisibilityToggleReceived;

    private readonly Action<string>? _onPttLocked;
    private readonly Action<string>? _onPttDenied;
    private readonly Action? _onPttReleased;

    private bool _isAttached;

    public LobbySignalRBinding(
        SignalRService signalR,
        Action<string, Color> onConnectionStatusChanged,
        Action<List<Rider>> onRosterUpdated,
        Action<double, double, string, bool> onNavigationStarted,
        Action<string, double, double, double> onRiderLocationUpdated,
        Action onNavigationCancelled,
        Action<string, string> onAlertReceived,
        Action<double, double, string> onDestinationSet,
        Action<string> onUserJoined,
        Action<string> onUserLeft,
        Action onGroupDeleted,
        Action<string, string> onNavigationPaused,
        Action<string> onNavigationResumed,
        Action<string> onNavigationCompleted,
        Action<string> onLeadRouteUpdated,
        Action<string> onRouteDeviationAlert,
        Action<double, double> onMeetupPointSet,
        Action<GroupSettingsDto> onGroupSettingsUpdated,
        Action<string, bool> onVisibilityToggleReceived,
        Action<string>? onPttLocked = null,
        Action<string>? onPttDenied = null,
        Action? onPttReleased = null)
    {
        _signalR = signalR;

        _onConnectionStatusChanged = onConnectionStatusChanged;
        _onRosterUpdated = onRosterUpdated;
        _onNavigationStarted = onNavigationStarted;
        _onRiderLocationUpdated = onRiderLocationUpdated;
        _onNavigationCancelled = onNavigationCancelled;
        _onAlertReceived = onAlertReceived;
        _onDestinationSet = onDestinationSet;
        _onUserJoined = onUserJoined;
        _onUserLeft = onUserLeft;
        _onGroupDeleted = onGroupDeleted;
        _onNavigationPaused = onNavigationPaused;
        _onNavigationResumed = onNavigationResumed;
        _onNavigationCompleted = onNavigationCompleted;
        _onLeadRouteUpdated = onLeadRouteUpdated;
        _onRouteDeviationAlert = onRouteDeviationAlert;
        _onMeetupPointSet = onMeetupPointSet;
        _onGroupSettingsUpdated = onGroupSettingsUpdated;
        _onVisibilityToggleReceived = onVisibilityToggleReceived;

        _onPttLocked = onPttLocked;
        _onPttDenied = onPttDenied;
        _onPttReleased = onPttReleased;
    }

    public void Attach()
    {
        if (_isAttached) return;

        _signalR.ConnectionStatusChanged += _onConnectionStatusChanged;
        _signalR.RosterUpdated += _onRosterUpdated;
        _signalR.NavigationStarted += _onNavigationStarted;
        _signalR.RiderLocationUpdated += _onRiderLocationUpdated;
        _signalR.NavigationCancelled += _onNavigationCancelled;
        _signalR.AlertReceived += _onAlertReceived;
        _signalR.DestinationSet += _onDestinationSet;
        _signalR.UserJoinedAlert += _onUserJoined;
        _signalR.UserLeftAlert += _onUserLeft;
        _signalR.GroupDeleted += _onGroupDeleted;
        _signalR.NavigationPaused += _onNavigationPaused;
        _signalR.NavigationResumed += _onNavigationResumed;
        _signalR.NavigationCompleted += _onNavigationCompleted;
        _signalR.LeadRouteUpdated += _onLeadRouteUpdated;
        _signalR.RouteDeviationAlert += _onRouteDeviationAlert;
        _signalR.MeetupPointSet += _onMeetupPointSet;
        _signalR.GroupSettingsUpdated += _onGroupSettingsUpdated;
        _signalR.VisibilityToggleReceived += _onVisibilityToggleReceived;

        if (_onPttLocked != null) _signalR.PttLocked += _onPttLocked;
        if (_onPttDenied != null) _signalR.PttDenied += _onPttDenied;
        if (_onPttReleased != null) _signalR.PttReleased += _onPttReleased;

        _isAttached = true;
    }

    public void Detach()
    {
        if (!_isAttached) return;

        _signalR.ConnectionStatusChanged -= _onConnectionStatusChanged;
        _signalR.RosterUpdated -= _onRosterUpdated;
        _signalR.NavigationStarted -= _onNavigationStarted;
        _signalR.RiderLocationUpdated -= _onRiderLocationUpdated;
        _signalR.NavigationCancelled -= _onNavigationCancelled;
        _signalR.AlertReceived -= _onAlertReceived;
        _signalR.DestinationSet -= _onDestinationSet;
        _signalR.UserJoinedAlert -= _onUserJoined;
        _signalR.UserLeftAlert -= _onUserLeft;
        _signalR.GroupDeleted -= _onGroupDeleted;
        _signalR.NavigationPaused -= _onNavigationPaused;
        _signalR.NavigationResumed -= _onNavigationResumed;
        _signalR.NavigationCompleted -= _onNavigationCompleted;
        _signalR.LeadRouteUpdated -= _onLeadRouteUpdated;
        _signalR.RouteDeviationAlert -= _onRouteDeviationAlert;
        _signalR.MeetupPointSet -= _onMeetupPointSet;
        _signalR.GroupSettingsUpdated -= _onGroupSettingsUpdated;
        _signalR.VisibilityToggleReceived -= _onVisibilityToggleReceived;

        if (_onPttLocked != null) _signalR.PttLocked -= _onPttLocked;
        if (_onPttDenied != null) _signalR.PttDenied -= _onPttDenied;
        if (_onPttReleased != null) _signalR.PttReleased -= _onPttReleased;

        _isAttached = false;
    }

    public void Dispose() => Detach();
}