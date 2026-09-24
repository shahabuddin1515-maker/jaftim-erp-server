using System.Data;
using Dapper;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Notifications;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>Procedures from Database/Notifications/*.sql in the legacy repo (NOTIFICATIONS.md section 3.2).</summary>
public sealed class NotificationRepository(IDbExecutor db) : INotificationRepository
{
    public Task<NotificationCreateResult> CreateAsync(NotificationRequest request, CancellationToken ct = default)
    {
        SpCall call = SpCall.Procedure("Notification_Create")
            .With("@TypeCode", request.TypeCode, DbType.AnsiString, 50)
            .With("@Title", request.Title, DbType.String, 200)
            .With("@Message", request.Message, DbType.String)
            .With("@EntityType", request.EntityType, DbType.String, 100)
            .With("@EntityId", request.EntityId)
            .With("@Url", request.Url, DbType.String, 500)
            .With("@Priority", request.Priority, DbType.Byte)
            .With("@RecipientUserIdsCsv", Csv(request.RecipientUserIds), DbType.String)
            .With("@RecipientRoleIdsCsv", Csv(request.RecipientRoleIds), DbType.String)
            .With("@ExcludeUserIdsCsv", Csv(request.ExcludeUserIds), DbType.String)
            .With("@UseRoleMap", request.UseRoleMap)
            .With("@ExcludeCreator", request.ExcludeCreator);

        return db.QueryMultipleAsync(call, async grid =>
        {
            NotificationPayload payload = await grid.ReadSingleAsync<NotificationPayload>();
            IReadOnlyList<long> recipients = (await grid.ReadAsync<long>()).AsList();
            return new NotificationCreateResult(payload, recipients);
        }, ct);
    }

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

    private static string? Csv(IReadOnlyCollection<long>? ids) =>
        ids is { Count: > 0 } ? string.Join(",", ids) : null;
}
