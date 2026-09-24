using System.Data;
using Dapper;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Notifications;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// NotificationConfig_* / NotificationType* procedures (legacy Database/Notifications/007_admin_config_procedures.sql,
/// NOTIFICATIONS.md section 11.4). Routing is pure data - Notification_Create reads it on every raise.
/// </summary>
public sealed class NotificationSettingsRepository(IDbExecutor db) : INotificationSettingsRepository
{
    public Task<IReadOnlyList<NotificationTypeConfig>> GetTypesAsync(CancellationToken ct = default) =>
        db.QueryAsync<NotificationTypeConfig>(SpCall.Procedure("NotificationConfig_GetTypes"), ct);

    public Task<NotificationTypeDetail?> GetTypeDetailAsync(int notificationTypeId, CancellationToken ct = default) =>
        db.QueryMultipleAsync<NotificationTypeDetail?>(
            SpCall.Procedure("NotificationConfig_GetTypeDetail").With("@NotificationTypeId", notificationTypeId),
            async grid =>
            {
                NotificationTypeSettings? settings = await grid.ReadFirstOrDefaultAsync<NotificationTypeSettings>();
                if (settings is null) return null;
                IReadOnlyList<long> roleIds = (await grid.ReadAsync<long>()).AsList();
                IReadOnlyList<NotificationUserOverride> overrides = (await grid.ReadAsync<NotificationUserOverride>()).AsList();
                return new NotificationTypeDetail(settings, roleIds, overrides);
            }, ct);

    public Task SaveSettingsAsync(int notificationTypeId, NotificationTypeSettingsRequest request, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("NotificationType_SaveSettings")
                .With("@NotificationTypeId", notificationTypeId)
                .With("@IsActive", request.IsActive)
                .With("@RecipientStrategy", (byte)request.RecipientStrategy, DbType.Byte)
                .With("@IncludeSystemAdmins", request.IncludeSystemAdmins), ct);

    /// <summary>Replaces the whole set: the procedure deactivates every mapping then re-activates/inserts the CSV.</summary>
    public Task SaveRolesAsync(int notificationTypeId, IReadOnlyList<long> roleIds, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("NotificationTypeRole_Save")
                .With("@NotificationTypeId", notificationTypeId)
                .With("@RoleIdsCsv", string.Join(",", roleIds), DbType.String), ct);

    public Task SaveUserOverrideAsync(int notificationTypeId, long userProfileId, NotificationUserOverrideMode mode, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("NotificationTypeUser_Save")
                .With("@NotificationTypeId", notificationTypeId)
                .With("@UserProfileId", userProfileId)
                .With("@Mode", (byte)mode, DbType.Byte), ct);

    public Task RemoveUserOverrideAsync(int notificationTypeId, long userProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("NotificationTypeUser_Remove")
                .With("@NotificationTypeId", notificationTypeId)
                .With("@UserProfileId", userProfileId), ct);

    public Task<IReadOnlyList<RoleOption>> GetRolesAsync(CancellationToken ct = default) =>
        db.QueryAsync<RoleOption>(SpCall.Procedure("NotificationConfig_GetRoles"), ct);

    public Task<IReadOnlyList<UserSearchResult>> SearchUsersAsync(string? query, CancellationToken ct = default) =>
        db.QueryAsync<UserSearchResult>(
            SpCall.Procedure("NotificationConfig_SearchUsers").With("@Query", query, DbType.String, 100), ct);
}
