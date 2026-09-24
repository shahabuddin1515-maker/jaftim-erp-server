using System.Data;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Lookups;
using Jaftim.Domain.Entities.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

public sealed class UserRepository(IDbExecutor db, IDateTimeProvider clock) : IUserRepository
{
    public Task<UserProfileWithRole?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<UserProfileWithRole>(
            SpCall.Procedure("UserGetByEmail")
                .With("@Email", email, DbType.String, 100)
                .WithoutAudit(), ct); // declares the audit params as optional; the caller is anonymous at login.

    public Task<UserProfileWithRole?> GetByIdAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<UserProfileWithRole>(
            SpCall.Text(ProfileWithRoleSelect + " WHERE UP.UserProfileId = @Id")
                .With("@Id", userProfileId)
                .WithoutAudit(), ct);

    public Task<UserProfileWithRole?> GetByAspNetUserIdAsync(string aspNetUserId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<UserProfileWithRole>(
            SpCall.Text(ProfileWithRoleSelect + " WHERE UP.AspNetUserId = @Id")
                .With("@Id", aspNetUserId, DbType.String, 450)
                .WithoutAudit(), ct);

    public Task<IReadOnlyList<RoleAction>> GetRoleActionsAsync(long roleId, CancellationToken ct = default) =>
        db.QueryAsync<RoleAction>(
            SpCall.Procedure("RoleActionGetByRoleId")
                .With("@RoleId", roleId), ct);

    public async Task<IReadOnlyList<string>> GetWhitelistedCidrsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LookupItem> rows = await db.QueryAsync<LookupItem>(SpCall.Procedure("GetWhitelistedIPs"), ct);
        return rows.Select(r => r.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
    }

    // Written from a background writer with no HTTP caller, so the actor is passed explicitly instead of injected.
    public Task SaveActionUrlAsync(string actionUrl, long userProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("SaveActionURL")
                .With("@ActionURL", actionUrl, DbType.String)
                .With("@CreatedBy", userProfileId)
                .With("@CreatedAt", clock.UtcNow)
                .With("@CompanyId", 1L)
                .WithoutAudit(), ct);

    public Task<IReadOnlyList<RoleAction>> GetAllRoleActionsAsync(CancellationToken ct = default) =>
        db.QueryAsync<RoleAction>(
            SpCall.Text("SELECT ActionId, ActionName, ActionCssClass, ActionMvcPermission, ActionParentId, ActionIdentification, DependantId FROM dbo.RoleAction ORDER BY ActionId")
                .WithoutAudit(), ct);

    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default) =>
        db.QueryAsync<Role>(SpCall.Procedure("RolesGetAll"), ct);

    // Inline: AspNetUsers is owned by the legacy Identity schema and has no procedure. Keeps SecurityStamp in step
    // so the legacy cookie sessions of that user are invalidated the way Identity expects.
    public Task MirrorPasswordHashAsync(string aspNetUserId, string passwordHash, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Text("UPDATE dbo.AspNetUsers SET PasswordHash = @Hash, SecurityStamp = @Stamp, ConcurrencyStamp = @Stamp WHERE Id = @Id AND ISNULL(PasswordHash, '') <> @Hash")
                .With("@Hash", passwordHash)
                .With("@Stamp", Guid.NewGuid().ToString())
                .With("@Id", aspNetUserId, DbType.String, 450)
                .WithoutAudit(), ct);

    public Task<IReadOnlyList<TenantCredentialRow>> GetCredentialRowsForSyncAsync(CancellationToken ct = default) =>
        db.QueryAsync<TenantCredentialRow>(
            SpCall.Text(
                """
                SELECT u.Id, u.Email, u.PasswordHash, u.LockoutEnabled, u.LockoutEnd, u.AccessFailedCount,
                       up.UserProfileId,
                       CAST(CASE WHEN up.StatusId = 2 AND ISNULL(up.IsDeleted, 0) = 0 THEN 1 ELSE 0 END AS BIT) AS IsActive
                FROM dbo.AspNetUsers u
                INNER JOIN dbo.UserProfile up ON up.AspNetUserId = u.Id
                WHERE u.Email IS NOT NULL AND u.Email <> ''
                """).WithoutAudit().WithTimeout(120), ct);

    // ----- staff CRUD (legacy UserController -> UserRepository) -----

    public Task<IReadOnlyList<UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default) =>
        db.QueryAsync<UserListItem>(SpCall.Procedure("UserGetAll").With("@UserTypeId", userTypeId), ct);

    public Task<UserDetail?> GetDetailAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<UserDetail>(SpCall.Procedure("UserGetById").With("@UserProfileId", userProfileId), ct);

    // Parameter list copied verbatim from the legacy UserRepository.UserSave. Known procedure quirks, preserved:
    // every INSERT also leaves a UserProfileDetail row with UserProfileId = 0 (unconditional delete+insert after the
    // insert branch), and only @CreatedBy = 1 may change RemoteAccessAllowed.
    public Task SaveAsync(UserSaveArgs a, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("UserSave")
                .With("@UserProfileId", a.UserProfileId)
                .With("@Code", a.Code, DbType.String, 50)
                .With("@FullName", a.FullName, DbType.String)
                .With("@Username", a.Username, DbType.String)
                .With("@Email", a.Email, DbType.String)
                .With("@URL", a.Url, DbType.String)
                .With("@StatusId", a.StatusId)
                .With("@AspNetUserId", a.AspNetUserId, DbType.String, 450)
                .With("@RoleId", a.RoleId)
                .With("@CustomerTagLimit", a.CustomerTagLimit, DbType.String)
                .With("@GenderId", a.GenderId)
                .With("@Address", a.Address, DbType.String)
                .With("@CountryCode", a.CountryCode, DbType.String, 10)
                .With("@CountryId", a.CountryId)
                .With("@CityId", a.CityId)
                .With("@ReportsTo_UserId", a.ReportsToUserId)
                .With("@OfficeLocation_CountryId", a.OfficeLocationCountryId)
                .With("@OfficialWhatsapp", a.OfficialWhatsapp, DbType.AnsiString, 200)
                .With("@RemoteAccessAllowed", a.RemoteAccessAllowed), ct);

    public Task ToggleActiveAsync(long userProfileId, CancellationToken ct = default) =>
        db.ExecuteAsync(SpCall.Procedure("ActiveInActiveCall").With("@userProfileId", userProfileId), ct);

    // What UserManager.CreateAsync wrote: a confirmed-email Identity row with lockout enabled (matches the API policy).
    public Task InsertAspNetUserAsync(string id, string email, string passwordHash, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Text(
                """
                INSERT INTO dbo.AspNetUsers (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash,
                                             SecurityStamp, ConcurrencyStamp, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
                VALUES (@Id, @Email, UPPER(@Email), @Email, UPPER(@Email), 1, @Hash, @Stamp, @Stamp, 0, 0, 1, 0)
                """)
                .With("@Id", id, DbType.String, 450)
                .With("@Email", email, DbType.String, 256)
                .With("@Hash", passwordHash)
                .With("@Stamp", Guid.NewGuid().ToString())
                .WithoutAudit(), ct);

    // Same projection UserGetByEmail returns, so all three lookups map to one model.
    private const string ProfileWithRoleSelect =
        """
        SELECT TOP 1 UP.*, UP.UserProfileId AS UserId, R.DefaultPath, R.RoleName, R.UserTypeId
        FROM dbo.UserProfile UP
        INNER JOIN dbo.Role R ON UP.RoleId = R.RoleId
        """;
}
