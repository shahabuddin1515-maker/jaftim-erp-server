using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Notifications;

namespace Jaftim.Application.Modules.Notifications;

/// <summary>The caller's own inbox (legacy NotificationController). Always scoped to the signed-in user.</summary>
public interface INotificationInboxService
{
    Task<NotificationSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<PagedResult<NotificationListItem>> GetListAsync(bool onlyUnread, PagedRequest paging, CancellationToken ct = default);
    Task MarkAllSeenAsync(CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
    Task MarkReadAsync(long notificationRecipientId, CancellationToken ct = default);
}

public sealed class NotificationInboxService(INotificationRepository repository, ICurrentUser user) : INotificationInboxService
{
    public Task<NotificationSummary> GetSummaryAsync(CancellationToken ct = default) =>
        repository.GetSummaryAsync(user.UserProfileId, ct);

    public Task<PagedResult<NotificationListItem>> GetListAsync(bool onlyUnread, PagedRequest paging, CancellationToken ct = default) =>
        repository.GetByUserAsync(user.UserProfileId, onlyUnread, paging, ct);

    public Task MarkAllSeenAsync(CancellationToken ct = default) => repository.MarkAllSeenAsync(user.UserProfileId, ct);

    public Task MarkAllReadAsync(CancellationToken ct = default) => repository.MarkAllReadAsync(user.UserProfileId, ct);

    // The procedure filters by RecipientUserId as well, so one user can never mark another user's item read.
    public Task MarkReadAsync(long notificationRecipientId, CancellationToken ct = default) =>
        repository.MarkReadAsync(notificationRecipientId, user.UserProfileId, ct);
}
