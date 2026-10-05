using Jaftim.Domain.Entities.Notifications;

namespace Jaftim.Application.Modules.Notifications;

/// <summary>
/// Mirrors NOTIFICATIONS.md: Notification_Create resolves recipients and returns (payload, recipient ids); the
/// payload is then pushed over SignalR to group "t{TenantId}-user-{UserProfileId}". Business services call
/// <see cref="INotificationDispatcher"/>; they never touch SignalR, SQL or the outbox directly.
/// </summary>
public sealed record NotificationRequest(
    string TypeCode,
    string? Title = null,
    string? Message = null,
    string? EntityType = null,
    long? EntityId = null,
    string? Url = null,
    byte? Priority = null,
    IReadOnlyCollection<long>? RecipientUserIds = null,
    IReadOnlyCollection<long>? RecipientRoleIds = null,
    IReadOnlyCollection<long>? ExcludeUserIds = null,
    bool UseRoleMap = true,
    bool ExcludeCreator = true);

/// <summary>
/// First result set of Notification_Create - what the bell receives over SignalR. A class with setters, not a
/// positional record: the procedure returns 12 columns, and Dapper cannot bind a record whose constructor does not
/// match them exactly (the first port's 9-field record failed on every call, so no push ever reached a client).
/// </summary>
public sealed class NotificationPayload
{
    public long NotificationId { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? EntityType { get; set; }
    public long? EntityId { get; set; }
    public string? Url { get; set; }
    public byte Priority { get; set; }
    public string? IconClass { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
}

public sealed record NotificationCreateResult(NotificationPayload Payload, IReadOnlyList<long> RecipientUserIds);

/// <summary>Base_NotificationType.Code constants (NOTIFICATIONS.md section 3.3).</summary>
public static class NotificationTypeCodes
{
    public const string StockCreated = "STOCK_CREATED";
    public const string StockReadyForSale = "STOCK_READY_FOR_SALE";
    public const string StockAvailableForSale = "STOCK_AVAILABLE_FOR_SALE";
    public const string BidRaised = "BID_RAISED";
    public const string BidApproved = "BID_APPROVED";
    public const string BidRejected = "BID_REJECTED";
    public const string DiscountRequestInitiated = "DISCOUNT_REQUEST_INITIATED";
    public const string DiscountRequestApproved = "DISCOUNT_REQUEST_APPROVED";
    public const string DiscountRequestRejected = "DISCOUNT_REQUEST_REJECTED";
    public const string ApprovalRequested = "APPROVAL_REQUESTED";
    public const string ApprovalApproved = "APPROVAL_APPROVED";
    public const string ApprovalRejected = "APPROVAL_REJECTED";
    public const string BankReconSubmitted = "BANK_RECON_SUBMITTED";
    public const string RefundRequestInitiated = "REFUND_REQUEST_INITIATED";
    public const string AdjustmentRequestInitiated = "ADJUSTMENT_REQUEST_INITIATED";
    public const string ConversionRequestInitiated = "CONVERSION_REQUEST_INITIATED";
    public const string CustomerTagged = "CUSTOMER_TAGGED";
    public const string CustomerUntagged = "CUSTOMER_UNTAGGED";
}

/// <summary>
/// Inbox reads and marks. Notification_Create is deliberately NOT here: it is only ever called by the notification
/// pipeline, inside a transaction with the outbox row (<see cref="INotificationOutboxRepository.PersistAsync"/>).
/// </summary>
public interface INotificationRepository
{
    /// <summary>EXEC Notification_GetSummary.</summary>
    Task<NotificationSummary> GetSummaryAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC Notification_GetByUser (two result sets: total count, then the page).</summary>
    Task<Common.PagedResult<NotificationListItem>> GetByUserAsync(long userProfileId, bool onlyUnread, Common.PagedRequest paging, CancellationToken ct = default);
    Task MarkAllSeenAsync(long userProfileId, CancellationToken ct = default);
    Task MarkAllReadAsync(long userProfileId, CancellationToken ct = default);
    Task MarkReadAsync(long notificationRecipientId, long userProfileId, CancellationToken ct = default);
}

/// <summary>Realtime delivery. Implemented in the API (SignalR hub) and as a no-op in Jobs.</summary>
public interface INotificationPusher
{
    Task PushAsync(NotificationPayload payload, IReadOnlyList<long> recipientUserIds, CancellationToken ct = default);
}

/// <summary>
/// The single entry point business code uses. Queues the event on the notification pipeline
/// (<see cref="NotificationPipeline"/>); delivery happens in the background. Best-effort: never throws into the
/// calling business flow.
/// </summary>
public interface INotificationDispatcher
{
    Task NotifyAsync(NotificationRequest request, CancellationToken ct = default);
}
