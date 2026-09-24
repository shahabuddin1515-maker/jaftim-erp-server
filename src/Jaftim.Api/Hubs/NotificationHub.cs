using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Jaftim.Api.Hubs;

/// <summary>
/// /hubs/notifications - same contract as the legacy hub (NOTIFICATIONS.md section 4.3): each connection joins group
/// "t{TenantId}-user-{UserProfileId}" and receives "ReceiveNotification" pushes. Clients authenticate with the JWT via the
/// access_token query string (SignalR JS client: accessTokenFactory) - see Program.cs JwtBearer OnMessageReceived.
/// </summary>
[Authorize]
public sealed class NotificationHub(ICurrentUser currentUser, ITenantContext tenant) : Hub
{
    public const string Path = "/hubs/notifications";
    public const string ClientMethod = "ReceiveNotification";

    // Profile ids repeat across tenant databases, so the group is keyed by tenant too.
    public static string GroupFor(int tenantId, long userProfileId) => $"t{tenantId}-user-{userProfileId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(tenant.TenantId, currentUser.UserProfileId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(tenant.TenantId, currentUser.UserProfileId));
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class SignalRNotificationPusher(IHubContext<NotificationHub> hub, ITenantContext tenant) : INotificationPusher
{
    public Task PushAsync(NotificationPayload payload, IReadOnlyList<long> recipientUserIds, CancellationToken ct = default) =>
        hub.Clients.Groups(recipientUserIds.Select(id => NotificationHub.GroupFor(tenant.TenantId, id)).ToArray())
            .SendAsync(NotificationHub.ClientMethod, payload, ct);
}
