using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Jaftim.Api.Hubs;

/// <summary>
/// /hubs/notifications - same contract as the legacy hub (NOTIFICATIONS.md section 4.3): each connection joins group
/// "t{TenantId}-user-{UserProfileId}" and receives "ReceiveNotification" pushes. Clients authenticate with the JWT via the
/// access_token query string (SignalR JS client: accessTokenFactory) - see Program.cs JwtBearer OnMessageReceived.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string ClientMethod = "ReceiveNotification";

    // Profile ids repeat across tenant databases, so the group is keyed by tenant too.
    public static string GroupFor(int tenantId, long userProfileId) => $"t{tenantId}-user-{userProfileId}";

    // The group comes from the connection's own token claims, NOT from the scoped ITenantContext: SignalR runs every
    // hub method in a fresh DI scope where TokenVersionValidator never bound the tenant, so that context reads
    // TenantId 0 here and the connection joined "t0-user-..." - a group no push ever targets (found 2026-10-05).
    private string? OwnGroup() =>
        int.TryParse(Context.User?.FindFirst(JaftimClaims.TenantId)?.Value, out int tenantId) && tenantId > 0
        && long.TryParse(Context.User?.FindFirst(JaftimClaims.UserProfileId)?.Value, out long userProfileId) && userProfileId > 0
            ? GroupFor(tenantId, userProfileId)
            : null;

    public override async Task OnConnectedAsync()
    {
        if (OwnGroup() is { } group) await Groups.AddToGroupAsync(Context.ConnectionId, group);
        else Context.Abort(); // a token without tenant/user claims can never receive anything
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (OwnGroup() is { } group) await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class SignalRNotificationPusher(IHubContext<NotificationHub> hub, ITenantContext tenant) : INotificationPusher
{
    public Task PushAsync(NotificationPayload payload, IReadOnlyList<long> recipientUserIds, CancellationToken ct = default) =>
        hub.Clients.Groups(recipientUserIds.Select(id => NotificationHub.GroupFor(tenant.TenantId, id)).ToArray())
            .SendAsync(NotificationHub.ClientMethod, payload, ct);
}
