using Jaftim.Api.Contracts;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// The caller's own inbox (legacy NotificationController). Scoped to the signed-in user, so no permission beyond
/// authentication - exactly as the bell behaved before. Realtime pushes arrive on /hubs/notifications.
/// </summary>
public sealed class NotificationsController(INotificationInboxService inbox) : ApiControllerBase
{
    /// <summary>Badge counts: unread items and unseen items (unseen resets when the bell is opened).</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<NotificationSummary>>> Summary(CancellationToken ct) =>
        Ok(await inbox.GetSummaryAsync(ct));

    /// <summary>Paged inbox (Notification_GetByUser). onlyUnread=true is what the bell dropdown uses.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<NotificationListItem>>>> List([FromQuery] bool onlyUnread, [FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await inbox.GetListAsync(onlyUnread, paging, ct));

    /// <summary>Clears the red badge (marks everything seen) without marking items read.</summary>
    [HttpPost("mark-all-seen")]
    public async Task<ActionResult<ApiResponse<object?>>> MarkAllSeen(CancellationToken ct)
    {
        await inbox.MarkAllSeenAsync(ct);
        return Ok();
    }

    [HttpPost("mark-all-read")]
    public async Task<ActionResult<ApiResponse<object?>>> MarkAllRead(CancellationToken ct)
    {
        await inbox.MarkAllReadAsync(ct);
        return Ok();
    }

    /// <summary>Marks one item read (and seen). The procedure scopes by recipient, so another user's item cannot be touched.</summary>
    [HttpPost("{notificationRecipientId:long}/mark-read")]
    public async Task<ActionResult<ApiResponse<object?>>> MarkRead(long notificationRecipientId, CancellationToken ct)
    {
        await inbox.MarkReadAsync(notificationRecipientId, ct);
        return Ok();
    }
}
