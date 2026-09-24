using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;

namespace Jaftim.Application.Modules.Auth;

/// <summary>
/// Identity + session persistence in the shared CATALOG database (Catalog_* procedures). Nothing here touches a
/// tenant database; the one tenant-side write (mirroring a password hash into AspNetUsers) is on IUserRepository.
/// </summary>
public interface IAuthRepository
{
    Task<Account?> GetAccountByEmailAsync(string email, CancellationToken ct = default);
    Task<Account?> GetAccountByIdAsync(string accountId, CancellationToken ct = default);
    /// <summary>byApi = true marks the catalog as authoritative for this account (see database/catalog/002_Seed_From_Tenant.md).</summary>
    Task UpdatePasswordHashAsync(string accountId, string passwordHash, long? actorUserProfileId, bool byApi, CancellationToken ct = default);
    Task RecordFailedAccessAsync(string accountId, int maxFailedAttempts, TimeSpan lockoutDuration, CancellationToken ct = default);
    Task ResetFailedAccessAsync(string accountId, CancellationToken ct = default);

    /// <summary>Invalidates every outstanding access token for the account (logout-all, password change, replay, deactivation).</summary>
    Task<int> BumpTokenVersionAsync(string accountId, CancellationToken ct = default);

    Task<IReadOnlyList<AccountMembership>> GetMembershipsAsync(string accountId, CancellationToken ct = default);
    /// <summary>Creates the account if new (or links an existing one with the same e-mail) and upserts the membership for this tenant. Returns the effective AccountId.</summary>
    Task<string> EnsureAccountAsync(string accountId, string email, string passwordHash, int tenantId, long userProfileId, bool isActive, CancellationToken ct = default);

    Task SaveRefreshTokenAsync(RefreshToken token, CancellationToken ct = default);
    Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByHash, CancellationToken ct = default);
    Task RevokeAllRefreshTokensAsync(string accountId, CancellationToken ct = default);

    /// <summary>Catalog AuthAuditLog. Events: Login, LoginFailed, Refresh, RefreshReplay, Logout, LogoutAll, SwitchTenant, PasswordChanged, Lockout.</summary>
    Task WriteAuthAuditAsync(AuthAuditEvent entry, CancellationToken ct = default);
}

public sealed record AuthAuditEvent(
    string Event,
    bool Success,
    string? Email,
    string? AccountId,
    int? TenantId,
    string? Detail,
    ClientInfo Client);
