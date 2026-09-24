using Jaftim.Domain.Entities.Notifications;
using Microsoft.Extensions.Logging;

namespace Jaftim.Application.Modules.Notifications;

/// <summary>
/// Mirrors NOTIFICATIONS.md: Notification_Create resolves recipients and returns (payload, recipient ids); the API
/// then pushes the payload over SignalR to group "user-{UserProfileId}". Business services call
/// <see cref="INotificationDispatcher"/>; they never touch SignalR or SQL directly.
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

public sealed record NotificationPayload(
    long NotificationId,
    string TypeCode,
    string Title,
    string? Message,
    string? EntityType,
    long? EntityId,
    string? Url,
    byte Priority,
    DateTime CreatedAt);

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

public interface INotificationRepository
{
    /// <summary>EXEC Notification_Create (two result sets: payload, recipient ids).</summary>
    Task<NotificationCreateResult> CreateAsync(NotificationRequest request, CancellationToken ct = default);
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

/// <summary>The single entry point business code uses. Best-effort: never throws into the calling business flow.</summary>
public interface INotificationDispatcher
{
    Task NotifyAsync(NotificationRequest request, CancellationToken ct = default);
}

public sealed class NotificationDispatcher(
    INotificationRepository repository,
    INotificationPusher pusher,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        try
        {
            NotificationCreateResult result = await repository.CreateAsync(request, ct);
            if (result.RecipientUserIds.Count > 0)
                await pusher.PushAsync(result.Payload, result.RecipientUserIds, ct);
        }
        catch (Exception ex)
        {
            // Preserved behaviour: a notification failure must never break the business action that raised it.
            logger.LogError(ex, "Notification {TypeCode} for {EntityType}/{EntityId} failed", request.TypeCode, request.EntityType, request.EntityId);
        }
    }
}
