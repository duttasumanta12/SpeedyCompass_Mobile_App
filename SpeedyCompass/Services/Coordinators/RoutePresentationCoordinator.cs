using Microsoft.Maui.Maps;
using SpeedyCompass.Engines;
using SpeedyCompass.Models;
using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass.Services.Coordinators;

public sealed class RoutePresentationCoordinator
{
    private readonly IRoutingEngine _routingEngine;
    private readonly RideStateService _rideCache;
    private readonly AppTierService _tierService;

    public RoutePresentationCoordinator(
        IRoutingEngine routingEngine,
        RideStateService rideCache,
        AppTierService tierService)
    {
        _routingEngine = routingEngine;
        _rideCache = rideCache;
        _tierService = tierService;
    }

    public async Task<RouteBuildResult?> BuildRouteAsync(RouteBuildRequest request)
    {
        RouteCalculationResult? routeData = null;

        // Fetch voice steps for active nav regardless of mode so immersive can be activated later without recalc
        var shouldIncludeVoiceSteps =
            request.IsMainRoute &&
            request.CurrentState >= GroupState.Navigating &&
            _tierService.UseImmersiveTbt;

        // Overlay rendering still depends on immersive mode
        var shouldGenerateImmersiveGuidance =
            shouldIncludeVoiceSteps &&
            request.NavigationMode == MapNavigationMode.Immersive;

        bool cachedHasRoute = _rideCache.CachedMainRouteData?.DecodedPoints?.Any() == true;
        bool cachedHasVoiceSteps = _rideCache.CachedMainRouteData?.VoiceSteps?.Any() == true;

        // If we need voice guidance but cache doesn't have steps, force one fetch (not mode-switch recalc)
        bool canReuseCache = cachedHasRoute && (!shouldIncludeVoiceSteps || cachedHasVoiceSteps);

        if (request.IsMainRoute && !request.IsReroute && canReuseCache)
        {
            var firstPt = _rideCache.CachedMainRouteData!.DecodedPoints.FirstOrDefault();
            if (firstPt != null &&
                Location.CalculateDistance(request.Origin, firstPt, DistanceUnits.Kilometers) < 0.5)
            {
                routeData = _rideCache.CachedMainRouteData;
            }
        }

        if (routeData == null && request.AllowNetworkFetch)
        {
            if (_tierService.UseGoogleRoutesApi)
            {
                routeData = await _routingEngine.GetRouteDataAsync(
                    request.Origin,
                    request.Destination,
                    request.MeetupPoint,
                    includeVoiceSteps: shouldIncludeVoiceSteps,
                    isReroute: request.IsReroute);
            }
            else
            {
                var points = new List<Location> { request.Origin };
                if (request.ActiveWaypoints != null && request.ActiveWaypoints.Any())
                {
                    var clean = request.ActiveWaypoints.ToList();
                    if (clean.Count > 1) clean.RemoveAt(0);
                    points.AddRange(clean);
                }
                else
                {
                    points.Add(request.Destination);
                }

                routeData = await _routingEngine.GetMapboxOverviewRouteAsync(points);
            }

            if (request.IsMainRoute && !request.IsReroute)
            {
                _rideCache.CachedMainRouteData = routeData;
            }
        }

        if (routeData == null || string.IsNullOrEmpty(routeData.EncodedPolyline))
            return null;

        var routeColor = request.RouteColor ?? (request.IsMainRoute ? Colors.DodgerBlue : request.DefaultSecondaryRouteColor);
        var routeUi = _routingEngine.BuildRouteVisuals(routeData, routeColor, shouldGenerateImmersiveGuidance, request.IsReroute);

        return new RouteBuildResult(
            routeUi,
            routeUi.EncodedPolyline,
            request.IsMainRoute,
            request.CurrentState >= GroupState.Navigating,
            request.IsReroute);
    }

    public MeetupDecision EvaluateMeetupProximity(
        Location currentLocation,
        Location? meetupPoint,
        bool haveIReachedMeetup,
        bool amIAdmin)
    {
        if (meetupPoint == null) return MeetupDecision.None;

        var distToMeetup = Location.CalculateDistance(currentLocation, meetupPoint, DistanceUnits.Kilometers);

        if (!haveIReachedMeetup && distToMeetup < 0.1)
        {
            return new MeetupDecision(
                MeetupDecisionAction.Reached,
                amIAdmin ? "Meetup point reached. Please wait here." : "You have reached the meetup point.");
        }

        if (haveIReachedMeetup && distToMeetup > 0.2 && amIAdmin)
        {
            return new MeetupDecision(MeetupDecisionAction.ClearForGroup, null);
        }

        return MeetupDecision.None;
    }
}

public sealed record RouteBuildRequest(
    Location Origin,
    Location Destination,
    Location? MeetupPoint,
    IReadOnlyList<Location>? ActiveWaypoints,
    bool IsMainRoute,
    bool IsReroute,
    bool AllowNetworkFetch,
    GroupState CurrentState,
    MapNavigationMode NavigationMode,
    Color? RouteColor,
    Color DefaultSecondaryRouteColor);

public sealed record RouteBuildResult(
    RouteUIData RouteUi,
    string EncodedPolyline,
    bool IsMainRoute,
    bool IsNavigating,
    bool IsReroute);

public enum MeetupDecisionAction
{
    None,
    Reached,
    ClearForGroup
}

public sealed record MeetupDecision(MeetupDecisionAction Action, string? VoiceMessage)
{
    public static MeetupDecision None { get; } = new(MeetupDecisionAction.None, null);
}