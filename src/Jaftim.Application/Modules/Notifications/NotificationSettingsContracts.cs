using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Notifications;
using Jaftim.Domain.Enums;
using Jaftim.Domain.Exceptions;
using Jaftim.Domain.Security;

namespace Jaftim.Application.Modules.Notifications;

/// <summary>Type-level settings (NotificationType_SaveSettings).</summary>
public sealed record NotificationTypeSettingsRequest(bool IsActive, NotificationRecipientStrategy RecipientStrategy, bool IncludeSystemAdmins);

/// <summary>Replaces the whole role set of a type (NotificationTypeRole_Save: deactivate all, re-activate/insert the list).</summary>
public sealed record NotificationTypeRolesRequest(IReadOnlyList<long> RoleIds);

/// <summary>Grant (Include) or deny (Exclude) one user regardless of their role (NotificationTypeUser_Save).</summary>
public sealed record NotificationUserOverrideRequest(long UserProfileId, NotificationUserOverrideMode Mode);

/// <summary>The admin side panel: settings + the role set + per-user overrides.</summary>
public sealed record NotificationTypeDetail(
    NotificationTypeSettings Settings,
    IReadOnlyList<long> RoleIds,
    IReadOnlyList<NotificationUserOverride> UserOverrides);

public sealed class NotificationTypeSettingsRequestValidator : AbstractValidator<NotificationTypeSettingsRequest>
{
    public NotificationTypeSettingsRequestValidator() =>
        RuleFor(x => x.RecipientStrategy).IsInEnum();
}

public sealed class NotificationTypeRolesRequestValidator : AbstractValidator<NotificationTypeRolesRequest>
{
    public NotificationTypeRolesRequestValidator()
    {
        RuleFor(x => x.RoleIds).NotNull();
        RuleForEach(x => x.RoleIds).GreaterThan(0);
    }
}

public sealed class NotificationUserOverrideRequestValidator : AbstractValidator<NotificationUserOverrideRequest>
{
    public NotificationUserOverrideRequestValidator()
    {
        RuleFor(x => x.UserProfileId).GreaterThan(0);
        RuleFor(x => x.Mode).IsInEnum();
    }
}

/// <summary>NotificationConfig_* / NotificationType* procedures (NOTIFICATIONS.md section 11.4).</summary>
public interface INotificationSettingsRepository
{
    Task<IReadOnlyList<NotificationTypeConfig>> GetTypesAsync(CancellationToken ct = default);
    Task<NotificationTypeDetail?> GetTypeDetailAsync(int notificationTypeId, CancellationToken ct = default);
    Task SaveSettingsAsync(int notificationTypeId, NotificationTypeSettingsRequest request, CancellationToken ct = default);
    Task SaveRolesAsync(int notificationTypeId, IReadOnlyList<long> roleIds, CancellationToken ct = default);
    Task SaveUserOverrideAsync(int notificationTypeId, long userProfileId, NotificationUserOverrideMode mode, CancellationToken ct = default);
    Task RemoveUserOverrideAsync(int notificationTypeId, long userProfileId, CancellationToken ct = default);
    Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserSearchResult>> SearchUsersAsync(string? query, CancellationToken ct = default);
}

public interface INotificationSettingsService
{
    Task<IReadOnlyList<NotificationTypeConfig>> GetTypesAsync(CancellationToken ct = default);
    Task<NotificationTypeDetail> GetTypeDetailAsync(int notificationTypeId, CancellationToken ct = default);
    Task<NotificationTypeDetail> SaveSettingsAsync(int notificationTypeId, NotificationTypeSettingsRequest request, CancellationToken ct = default);
    Task<NotificationTypeDetail> SaveRolesAsync(int notificationTypeId, NotificationTypeRolesRequest request, CancellationToken ct = default);
    Task<NotificationTypeDetail> SaveUserOverrideAsync(int notificationTypeId, NotificationUserOverrideRequest request, CancellationToken ct = default);
    Task<NotificationTypeDetail> RemoveUserOverrideAsync(int notificationTypeId, long userProfileId, CancellationToken ct = default);
    Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserSearchResult>> SearchUsersAsync(string? query, CancellationToken ct = default);
}

/// <summary>
/// Who receives which event, as data (legacy NotificationSettingsController, gated there on RoleId IN (1,10) -
/// here on the Notification Settings permission). Routing changes take effect on the next raise, no deploy and no
/// cache: Notification_Create reads the config every time. Every change is audited.
/// </summary>
public sealed class NotificationSettingsService(
    INotificationSettingsRepository repository,
    Users.IUserRepository users,
    IAuditWriter audit,
    IValidator<NotificationTypeSettingsRequest> settingsValidator,
    IValidator<NotificationTypeRolesRequest> rolesValidator,
    IValidator<NotificationUserOverrideRequest> overrideValidator) : INotificationSettingsService
{
    public Task<IReadOnlyList<NotificationTypeConfig>> GetTypesAsync(CancellationToken ct = default) =>
        repository.GetTypesAsync(ct);

    public async Task<NotificationTypeDetail> GetTypeDetailAsync(int notificationTypeId, CancellationToken ct = default) =>
        await repository.GetTypeDetailAsync(notificationTypeId, ct)
            ?? throw new NotFoundException("Notification type", notificationTypeId);

    public async Task<NotificationTypeDetail> SaveSettingsAsync(int notificationTypeId, NotificationTypeSettingsRequest request, CancellationToken ct = default)
    {
        await settingsValidator.ValidateAndThrowAppAsync(request, ct);
        NotificationTypeDetail before = await GetTypeDetailAsync(notificationTypeId, ct);

        await repository.SaveSettingsAsync(notificationTypeId, request, ct);
        NotificationTypeDetail after = await GetTypeDetailAsync(notificationTypeId, ct);

        await audit.RecordAsync("NotificationType.SettingsChanged", "Base_NotificationType", notificationTypeId, before.Settings, after.Settings, ct);
        return after;
    }

    public async Task<NotificationTypeDetail> SaveRolesAsync(int notificationTypeId, NotificationTypeRolesRequest request, CancellationToken ct = default)
    {
        await rolesValidator.ValidateAndThrowAppAsync(request, ct);
        NotificationTypeDetail before = await GetTypeDetailAsync(notificationTypeId, ct);

        long[] roleIds = request.RoleIds.Distinct().ToArray();
        IReadOnlyList<RoleOption> roles = await repository.GetRolesAsync(ct);
        long[] unknown = roleIds.Where(id => roles.All(r => r.RoleId != id)).ToArray();
        if (unknown.Length > 0)
            throw new BusinessRuleException($"Unknown role id(s): {string.Join(", ", unknown)}.");

        await repository.SaveRolesAsync(notificationTypeId, roleIds, ct);
        NotificationTypeDetail after = await GetTypeDetailAsync(notificationTypeId, ct);

        await audit.RecordAsync("NotificationType.RolesChanged", "Base_NotificationType", notificationTypeId,
            new { before.RoleIds }, new { after.RoleIds }, ct);
        return after;
    }

    public async Task<NotificationTypeDetail> SaveUserOverrideAsync(int notificationTypeId, NotificationUserOverrideRequest request, CancellationToken ct = default)
    {
        await overrideValidator.ValidateAndThrowAppAsync(request, ct);
        NotificationTypeDetail before = await GetTypeDetailAsync(notificationTypeId, ct);

        // Same rule as the picker procedure (NotificationConfig_SearchUsers): active staff only, so a customer,
        // an inactive account or a deleted profile can never be granted or denied an event.
        UserProfileWithRole? target = await users.GetByIdAsync(request.UserProfileId, ct);
        if (target is null || target.StatusId != (int)UserStatus.Active || (target.IsDeleted ?? 0) != 0 || target.RoleId == RoleIds.Customer)
            throw new NotFoundException("Staff user", request.UserProfileId);

        await repository.SaveUserOverrideAsync(notificationTypeId, request.UserProfileId, request.Mode, ct);
        NotificationTypeDetail after = await GetTypeDetailAsync(notificationTypeId, ct);

        await audit.RecordAsync("NotificationType.UserOverrideSaved", "Base_NotificationType", notificationTypeId,
            before.UserOverrides.FirstOrDefault(o => o.UserProfileId == request.UserProfileId),
            after.UserOverrides.FirstOrDefault(o => o.UserProfileId == request.UserProfileId), ct);
        return after;
    }

    public async Task<NotificationTypeDetail> RemoveUserOverrideAsync(int notificationTypeId, long userProfileId, CancellationToken ct = default)
    {
        NotificationTypeDetail before = await GetTypeDetailAsync(notificationTypeId, ct);
        NotificationUserOverride existing = before.UserOverrides.FirstOrDefault(o => o.UserProfileId == userProfileId)
            ?? throw new NotFoundException("User override", userProfileId);

        await repository.RemoveUserOverrideAsync(notificationTypeId, userProfileId, ct);
        NotificationTypeDetail after = await GetTypeDetailAsync(notificationTypeId, ct);

        await audit.RecordAsync("NotificationType.UserOverrideRemoved", "Base_NotificationType", notificationTypeId, existing, null, ct);
        return after;
    }

    public Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken ct = default) => repository.GetRolesAsync(ct);

    public Task<IReadOnlyList<UserSearchResult>> SearchUsersAsync(string? query, CancellationToken ct = default) =>
        repository.SearchUsersAsync(query?.Trim(), ct);
}
