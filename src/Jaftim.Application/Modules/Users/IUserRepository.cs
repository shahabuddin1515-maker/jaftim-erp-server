using Jaftim.Domain.Entities.Users;

namespace Jaftim.Application.Modules.Users;

/// <summary>UserProfile / Role / RoleAction reads and the per-request audit hooks the legacy BaseController ran.</summary>
public interface IUserRepository
{
    /// <summary>EXEC UserGetByEmail - UserProfile joined to Role (RoleName, DefaultPath, UserTypeId).</summary>
    Task<UserProfileWithRole?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<UserProfileWithRole?> GetByIdAsync(long userProfileId, CancellationToken ct = default);
    Task<UserProfileWithRole?> GetByAspNetUserIdAsync(string aspNetUserId, CancellationToken ct = default);

    /// <summary>EXEC RoleActionGetByRoleId - the role's RoleAction rows (its permission set).</summary>
    Task<IReadOnlyList<RoleAction>> GetRoleActionsAsync(long roleId, CancellationToken ct = default);

    /// <summary>EXEC GetWhitelistedIPs - CIDR list from Base_WhitelistedIPs.</summary>
    Task<IReadOnlyList<string>> GetWhitelistedCidrsAsync(CancellationToken ct = default);

    /// <summary>EXEC SaveActionURL - the legacy per-request URL audit (ActionURlTbl).</summary>
    Task SaveActionUrlAsync(string actionUrl, long userProfileId, CancellationToken ct = default);

    /// <summary>Every RoleAction row (the permission catalog) - RoleAction has no list procedure.</summary>
    Task<IReadOnlyList<RoleAction>> GetAllRoleActionsAsync(CancellationToken ct = default);

    /// <summary>EXEC RolesGetAll (tenant Role table).</summary>
    Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// Mirrors a catalog password change into the tenant AspNetUsers row so the legacy MVC app (and any procedure
    /// that reads the hash) keeps working during coexistence. No-op if the row does not exist in this tenant.
    /// </summary>
    Task MirrorPasswordHashAsync(string aspNetUserId, string passwordHash, CancellationToken ct = default);

    /// <summary>Rows of tenant AspNetUsers joined to UserProfile - the source for the catalog account sync.</summary>
    Task<IReadOnlyList<TenantCredentialRow>> GetCredentialRowsForSyncAsync(CancellationToken ct = default);

    // ----- staff CRUD (legacy UserController) -----

    /// <summary>EXEC UserGetAll  - visibility branches on the caller PRIMARY role inside the procedure.</summary>
    Task<IReadOnlyList<UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default);
    /// <summary>EXEC UserGetById.</summary>
    Task<UserDetail?> GetDetailAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC UserSave (insert when UserProfileId = 0). Returns nothing - fetch the new row by AspNetUserId.</summary>
    Task SaveAsync(UserSaveArgs args, CancellationToken ct = default);
    /// <summary>EXEC ActiveInActiveCall.</summary>
    Task ToggleActiveAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>Inserts the tenant AspNetUsers row the legacy UserManager.CreateAsync used to create (no procedure exists).</summary>
    Task InsertAspNetUserAsync(string id, string email, string passwordHash, CancellationToken ct = default);
}

/// <summary>One AspNetUsers row with its UserProfile, as read by AccountSyncJob.</summary>
public sealed class TenantCredentialRow
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public long UserProfileId { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>UserGetByEmail row: UserProfile.* plus Role columns.</summary>
public sealed class UserProfileWithRole
{
    public long UserProfileId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Username { get; set; }
    public string? AspNetUserId { get; set; }
    public long RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public int UserTypeId { get; set; }
    public string? DefaultPath { get; set; }
    public int StatusId { get; set; }
    public long? CompanyId { get; set; }
    public long? ReportsTo_UserId { get; set; }
    public bool? RemoteAccessAllowed { get; set; }
    public long? IsDeleted { get; set; }
    public string? ImageURL { get; set; }
}
