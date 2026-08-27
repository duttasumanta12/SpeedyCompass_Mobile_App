using Microsoft.Maui.Maps;
using SpeedyCompass.Services.Coordinators;
using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass;

public partial class LobbyPage
{
    private RoutePresentationCoordinator? _routeCoordinator;

    private void InitializeRouteCoordinator()
    {
        _routeCoordinator = new RoutePresentationCoordinator(_routingEngine, _rideCache, _tierService);
    }

    private async Task<string?> CalculateAndDrawRoute(
        Location origin,
        Location dest,
        Location? meetup = null,
        Color? routeColor = null,
        string? riderName = null,
        bool isMainRoute = true,
        bool isReroute = false,
        bool allowNetworkFetch = true,
        bool isPreview = false)
    {
        var request = new RouteBuildRequest(
            origin,
            dest,
            meetup,
            _rideCache.ActiveWaypoints,
            isMainRoute,
            isReroute,
            allowNetworkFetch,
            groupDetails.CurrentState,
            _currentNavMode,
            routeColor,
            GetColorsForRider(CurrentGoogleId).RouteColor);

        var result = await _routeCoordinator!.BuildRouteAsync(request);
        if (result == null) return null;

        ApplyRouteUi(result.RouteUi, result.IsMainRoute, result.IsNavigating, result.IsReroute);
        return result.EncodedPolyline;
    }

    private async Task CheckMeetupProximityUnifiedAsync(Location currentLocation)
    {
        var decision = _routeCoordinator!.EvaluateMeetupProximity(
            currentLocation,
            _rideCache.ActiveMeetupPoint,
            _rideCache.HaveIReachedMeetup,
            _amIAdmin);

        switch (decision.Action)
        {
            case MeetupDecisionAction.Reached:
                _rideCache.HaveIReachedMeetup = true;
                if (!string.IsNullOrWhiteSpace(decision.VoiceMessage))
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _voiceEngine.Speak(decision.VoiceMessage);
                        _signalRService.SendGroupAlert(GroupNameLabel.Text, "MeetupArrival", _myName).SafeFireAndForget();
                    });
                }
                break;

            case MeetupDecisionAction.ClearForGroup:
                _rideCache.HaveIReachedMeetup = false;
                await _signalRService.SetGroupMeetupPoint(GroupNameLabel.Text, 0, 0);
                break;
        }
    }
}