using System.Data;
using Dapper;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Notifications;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// Procedures from Database/Notifications/*.sql in the legacy repo (NOTIFICATIONS.md section 3.2). Notification_Create
/// lives in <see cref="NotificationOutboxRepository.PersistAsync"/> - only the pipeline calls it.
/// </summary>
public sealed class NotificationRepository(IDbExecutor db) : INotificationRepository
{
    public Task<NotificationSummary> GetSummaryAsync(long userProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<NotificationSummary>(
            SpCall.Procedure("Notification_GetSummary").With("@UserId", userProfileId), ct);

    public Task<PagedResult<NotificationListItem>> GetByUserAsync(long userProfileId, bool onlyUnread, PagedRequest paging, CancellationToken ct = default) =>
        db.QueryMultipleAsync(
            SpCall.Procedure("Notification_GetByUser")
                .With("@UserId", userProfileId)
                .With("@OnlyUnread", onlyUnread)
                .With("@Skip", paging.Skip)
                .With("@Take", paging.PageSize),
            async grid =>
            {
                int total = await grid.ReadSingleAsync<int>();
                IReadOnlyList<NotificationListItem> rows = (await grid.ReadAsync<NotificationListItem>()).AsList();
                return new PagedResult<NotificationListItem>(rows, paging.Page, paging.PageSize, total);
            }, ct);

    public Task MarkAllSeenAsync(long userProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(SpCall.Procedure("Notification_MarkAllSeen").With("@UserId", userProfileId), ct);

    public Task MarkAllReadAsync(long userProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(SpCall.Procedure("Notification_MarkAllRead").With("@UserId", userProfileId), ct);

    public Task MarkReadAsync(long notificationRecipientId, long userProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("Notification_MarkRead")
                .With("@NotificationRecipientId", notificationRecipientId)
                .With("@UserId", userProfileId), ct);
}
