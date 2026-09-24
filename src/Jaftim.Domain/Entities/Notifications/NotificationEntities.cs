namespace Jaftim.Domain.Entities.Notifications;

/// <summary>Notification_GetByUser row - one inbox item for the bell/list.</summary>
public sealed class NotificationListItem
{
    public long NotificationRecipientId { get; set; }
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
    public bool IsSeen { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? CreatedAt { get; set; }
    /// <summary>Who raised it (Notification.CreatedBy resolved to a name).</summary>
    public string? CreatedByName { get; set; }
    /// <summary>Customer the entity resolves to, for deep links when My Task is not available to the reader.</summary>
    public long? RelatedCustomerId { get; set; }
}

/// <summary>
/// Base_NotificationType.RecipientStrategy - the BASE audience of an event before role map, system admins,
/// explicit recipients and per-user grant/deny are composed in (NOTIFICATIONS.md section 11.1).
/// </summary>
public enum NotificationRecipientStrategy : byte
{
    /// <summary>Active users in the type's NotificationTypeRoleMap roles.</summary>
    RoleMap = 1,
    /// <summary>The actor's managers up UserProfile.ReportsTo_UserId (fn_GetUserParentProfiles).</summary>
    ReportingChainUp = 2,
    /// <summary>Nobody by default - only users the raising code passes explicitly.</summary>
    ExplicitOnly = 3,
}

/// <summary>NotificationTypeUserMap.Mode - a per-user override that always wins over role/strategy.</summary>
public enum NotificationUserOverrideMode : byte
{
    Include = 1,
    Exclude = 2,
}

/// <summary>NotificationConfig_GetTypes row - the admin grid.</summary>
public sealed class NotificationTypeConfig
{
    public int NotificationTypeId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? IconClass { get; set; }
    public byte Priority { get; set; }
    public NotificationRecipientStrategy RecipientStrategy { get; set; }
    public bool IncludeSystemAdmins { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Active NotificationTypeRoleMap role ids, comma separated (as the procedure emits them).</summary>
    public string? RoleIdsCsv { get; set; }
    public int UserOverrideCount { get; set; }
}

/// <summary>First result set of NotificationConfig_GetTypeDetail.</summary>
public sealed class NotificationTypeSettings
{
    public int NotificationTypeId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? IconClass { get; set; }
    public byte Priority { get; set; }
    public NotificationRecipientStrategy RecipientStrategy { get; set; }
    public bool IncludeSystemAdmins { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Third result set of NotificationConfig_GetTypeDetail.</summary>
public sealed class NotificationUserOverride
{
    public long UserProfileId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? RoleName { get; set; }
    public NotificationUserOverrideMode Mode { get; set; }
}

/// <summary>NotificationConfig_GetRoles row.</summary>
public sealed class RoleOption
{
    public long RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
}

/// <summary>NotificationConfig_SearchUsers row (active staff only).</summary>
public sealed class UserSearchResult
{
    public long UserProfileId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? RoleName { get; set; }
}
