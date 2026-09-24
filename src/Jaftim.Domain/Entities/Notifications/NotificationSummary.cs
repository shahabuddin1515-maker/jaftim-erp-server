namespace Jaftim.Domain.Entities.Notifications;

/// <summary>Notification_GetSummary result.</summary>
public sealed record NotificationSummary(int UnreadCount, int UnseenCount);
