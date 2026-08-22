using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using SpeedyCompass.Backend.Hubs;
using SpeedyCompass.Backend.Models;
using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass.Backend.Services.Alerts;

public interface IRoleAlertDispatcher
{
    Task DispatchAsync(AlertType type, AlertContext context);
}

public sealed class RoleAlertDispatcher : IRoleAlertDispatcher
{
    private readonly CompassStateManager _state;
    private readonly IHubContext<CompassHub> _hub;
    private readonly IReadOnlyDictionary<AlertType, IRoleAlertPolicy> _policies;

    public RoleAlertDispatcher(
        CompassStateManager state,
        IHubContext<CompassHub> hub,
        IEnumerable<IRoleAlertPolicy> policies)
    {
        _state = state;
        _hub = hub;
        _policies = policies.ToDictionary(p => p.Type);
    }

    public async Task DispatchAsync(AlertType type, AlertContext context)
    {
        if (!_policies.TryGetValue(type, out var policy)) return;

        var members = await _state.GroupMembers
            .Find(m => m.GroupName == context.GroupName && m.IsOnline && !string.IsNullOrEmpty(m.ConnectionId))
            .ToListAsync();

        var recipients = members.Where(m => MatchesAudience(m, policy.Audience)).ToList();
        if (recipients.Count == 0) return;

        var payload = policy.BuildPayload(context);

        foreach (var r in recipients)
        {
            await _hub.Clients.Client(r.ConnectionId!)
                .SendAsync("ReceiveAlert", policy.OutboundAlertType, payload);
        }
    }

    private static bool MatchesAudience(GroupMember m, NotificationAudience audience)
    {
        if (audience.HasFlag(NotificationAudience.Everyone)) return true;

        if (m.IsAdmin && audience.HasFlag(NotificationAudience.Admin)) return true;
        return m.Role switch
        {
            "Lead" => audience.HasFlag(NotificationAudience.Lead),
            "Tail" => audience.HasFlag(NotificationAudience.Tail),
            "Marshal" => audience.HasFlag(NotificationAudience.Marshal),
            _ => audience.HasFlag(NotificationAudience.Rider)
        };
    }
}