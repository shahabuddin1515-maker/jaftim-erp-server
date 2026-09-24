using System.Data;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Auth;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories.Catalog;

/// <summary>Catalog_* procedures (database/catalog/001_Catalog_Schema.sql). Catalog-bound: no legacy audit trio.</summary>
public sealed class AuthRepository(ICatalogDbExecutor catalog, IDateTimeProvider clock) : IAuthRepository
{
    public Task<Account?> GetAccountByEmailAsync(string email, CancellationToken ct = default) =>
        catalog.QueryFirstOrDefaultAsync<Account>(
            SpCall.Procedure("Catalog_Account_GetByEmail").With("@NormalizedEmail", email.ToUpperInvariant(), DbType.String, 256).WithoutAudit(), ct);

    public Task<Account?> GetAccountByIdAsync(string accountId, CancellationToken ct = default) =>
        catalog.QueryFirstOrDefaultAsync<Account>(
            SpCall.Procedure("Catalog_Account_GetById").With("@AccountId", accountId, DbType.String, 450).WithoutAudit(), ct);

    public Task UpdatePasswordHashAsync(string accountId, string passwordHash, long? actorUserProfileId, bool byApi, CancellationToken ct = default) =>
        catalog.ExecuteAsync(
            SpCall.Procedure("Catalog_Account_UpdatePasswordHash")
                .With("@AccountId", accountId, DbType.String, 450)
                .With("@PasswordHash", passwordHash)
                .With("@ModifiedBy", actorUserProfileId)
                .With("@ByApi", byApi)
                .WithoutAudit(), ct);

    public Task RecordFailedAccessAsync(string accountId, int maxFailedAttempts, TimeSpan lockoutDuration, CancellationToken ct = default) =>
        catalog.ExecuteAsync(
            SpCall.Procedure("Catalog_Account_RecordFailedAccess")
                .With("@AccountId", accountId, DbType.String, 450)
                .With("@MaxFailedAttempts", maxFailedAttempts)
                .With("@LockoutEnd", new DateTimeOffset(clock.UtcNow.Add(lockoutDuration)))
                .WithoutAudit(), ct);

    public Task ResetFailedAccessAsync(string accountId, CancellationToken ct = default) =>
        catalog.ExecuteAsync(SpCall.Procedure("Catalog_Account_ResetFailedAccess").With("@AccountId", accountId, DbType.String, 450).WithoutAudit(), ct);

    public Task<int> BumpTokenVersionAsync(string accountId, CancellationToken ct = default) =>
        catalog.QuerySingleAsync<int>(SpCall.Procedure("Catalog_Account_BumpTokenVersion").With("@AccountId", accountId, DbType.String, 450).WithoutAudit(), ct);

    public Task<IReadOnlyList<AccountMembership>> GetMembershipsAsync(string accountId, CancellationToken ct = default) =>
        catalog.QueryAsync<AccountMembership>(SpCall.Procedure("Catalog_AccountTenant_GetByAccount").With("@AccountId", accountId, DbType.String, 450).WithoutAudit(), ct);

    // Same upsert AccountSyncJob uses, so a user created through the API and one discovered by the sync converge on one row.
    public Task<string> EnsureAccountAsync(string accountId, string email, string passwordHash, int tenantId, long userProfileId, bool isActive, CancellationToken ct = default) =>
        catalog.QuerySingleAsync<string>(
            SpCall.Procedure("Catalog_Account_UpsertFromTenant")
                .With("@AccountId", accountId, DbType.String, 450)
                .With("@Email", email, DbType.String, 256)
                .With("@PasswordHash", passwordHash)
                .With("@LockoutEnabled", true)
                .With("@LockoutEnd", null, DbType.DateTimeOffset)
                .With("@AccessFailedCount", 0)
                .With("@TenantId", tenantId)
                .With("@UserProfileId", userProfileId)
                .With("@IsActive", isActive)
                .WithoutAudit(), ct);

    public Task SaveRefreshTokenAsync(RefreshToken token, CancellationToken ct = default) =>
        catalog.ExecuteAsync(
            SpCall.Procedure("Catalog_RefreshToken_Save")
                .With("@AccountId", token.AccountId, DbType.String, 450)
                .With("@TenantId", token.TenantId)
                .With("@UserProfileId", token.UserProfileId)
                .With("@TokenHash", token.TokenHash, DbType.AnsiStringFixedLength, 64)
                .With("@ExpiresAtUtc", token.ExpiresAtUtc)
                .With("@CreatedAtUtc", token.CreatedAtUtc)
                .With("@CreatedByIp", token.CreatedByIp, DbType.String, 64)
                .With("@UserAgent", token.UserAgent, DbType.String, 400)
                .WithoutAudit(), ct);

    public Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default) =>
        catalog.QueryFirstOrDefaultAsync<RefreshToken>(
            SpCall.Procedure("Catalog_RefreshToken_Get").With("@TokenHash", tokenHash, DbType.AnsiStringFixedLength, 64).WithoutAudit(), ct);

    public Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByHash, CancellationToken ct = default) =>
        catalog.ExecuteAsync(
            SpCall.Procedure("Catalog_RefreshToken_Revoke")
                .With("@TokenHash", tokenHash, DbType.AnsiStringFixedLength, 64)
                .With("@ReplacedByTokenHash", replacedByHash, DbType.AnsiStringFixedLength, 64)
                .With("@RevokedAtUtc", clock.UtcNow)
                .WithoutAudit(), ct);

    public Task RevokeAllRefreshTokensAsync(string accountId, CancellationToken ct = default) =>
        catalog.ExecuteAsync(
            SpCall.Procedure("Catalog_RefreshToken_RevokeAll")
                .With("@AccountId", accountId, DbType.String, 450)
                .With("@RevokedAtUtc", clock.UtcNow)
                .WithoutAudit(), ct);

    public Task WriteAuthAuditAsync(AuthAuditEvent e, CancellationToken ct = default) =>
        catalog.ExecuteAsync(
            SpCall.Procedure("Catalog_AuthAudit_Write")
                .With("@Email", e.Email, DbType.String, 256)
                .With("@AccountId", e.AccountId, DbType.String, 450)
                .With("@TenantId", e.TenantId)
                .With("@Event", e.Event, DbType.String, 50)
                .With("@Success", e.Success)
                .With("@Detail", Truncate(e.Detail, 400), DbType.String, 400)
                .With("@Ip", Truncate(e.Client.IpAddress, 64), DbType.String, 64)
                .With("@UserAgent", Truncate(e.Client.UserAgent, 400), DbType.String, 400)
                .WithoutAudit(), ct);

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
