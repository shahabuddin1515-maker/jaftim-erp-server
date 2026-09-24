using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Enums;
using Jaftim.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace Jaftim.Application.Modules.Auth;

/// <summary>
/// JWT login/refresh/switch/logout. Credentials and sessions live in the shared catalog (Account, AccountTenant,
/// AccountRefreshToken); the user's profile, roles and permissions live in the chosen tenant database.
///
/// Preserved legacy behaviour: an inactive UserProfile is rejected after a correct password; every attempt is
/// audited; CompanyId is 1. New: lockout is ON, tenant selection, replay detection, token-version revocation.
/// </summary>
public sealed class AuthService(
    IAuthRepository catalog,
    IUserRepository users,
    IPermissionService permissions,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    ICurrentUser currentUser,
    ITenantContext tenant,
    ITenantContextSetter tenantSetter,
    IDateTimeProvider clock,
    IOptions<AuthOptions> options) : IAuthService
{
    private const int LegacyCompanyId = 1;
    private readonly AuthOptions _options = options.Value;

    public async Task<TokenResponse> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken ct = default)
    {
        string email = request.Email.Trim();
        Account? account = await catalog.GetAccountByEmailAsync(email, ct);

        if (account?.PasswordHash is null || !account.IsActive)
        {
            await AuditAsync("LoginFailed", false, email, account?.AccountId, null, "invalid attempt", client, ct);
            throw new ForbiddenException("Invalid email or password.");
        }

        if (account.LockoutEnabled && account.LockoutEnd is { } lockoutEnd && lockoutEnd > clock.UtcNow)
        {
            await AuditAsync("Lockout", false, email, account.AccountId, null, "user locked out", client, ct);
            throw new ForbiddenException("Account is temporarily locked. Try again later.");
        }

        PasswordVerificationResult verification = passwordHasher.Verify(account.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await catalog.RecordFailedAccessAsync(account.AccountId, _options.MaxFailedAccessAttempts, TimeSpan.FromMinutes(_options.LockoutMinutes), ct);
            await AuditAsync("LoginFailed", false, email, account.AccountId, null, "invalid attempt", client, ct);
            throw new ForbiddenException("Invalid email or password.");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            await catalog.UpdatePasswordHashAsync(account.AccountId, passwordHasher.Hash(request.Password), null, byApi: false, ct);

        IReadOnlyList<AccountMembership> memberships = await catalog.GetMembershipsAsync(account.AccountId, ct);
        AccountMembership membership = ChooseMembership(memberships, request.TenantCode);

        UserProfileWithRole profile = await LoadActiveProfileAsync(membership, ct)
            ?? throw await RejectInactiveAsync(email, account.AccountId, membership.TenantId, client, ct);

        await catalog.ResetFailedAccessAsync(account.AccountId, ct);
        await AuditAsync("Login", true, email, account.AccountId, membership.TenantId, null, client, ct);
        return await IssueTokensAsync(account, membership, memberships, profile, client, ct);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshTokenRequest request, ClientInfo client, CancellationToken ct = default)
    {
        string hash = tokenService.HashRefreshToken(request.RefreshToken);
        RefreshToken? stored = await catalog.GetRefreshTokenAsync(hash, ct);
        if (stored is null)
            throw new ForbiddenException("Invalid refresh token.");

        if (!stored.IsActive)
        {
            // Reuse of a rotated/revoked token is a replay signal: kill every session of this account, including
            // access tokens already in flight (TokenVersion bump; picked up within the validator cache window).
            await catalog.RevokeAllRefreshTokensAsync(stored.AccountId, ct);
            await catalog.BumpTokenVersionAsync(stored.AccountId, ct);
            await AuditAsync("RefreshReplay", false, null, stored.AccountId, stored.TenantId, "revoked token reused; all sessions revoked", client, ct);
            throw new ForbiddenException("Refresh token is no longer valid.");
        }

        Account account = await catalog.GetAccountByIdAsync(stored.AccountId, ct) ?? throw new ForbiddenException("Invalid refresh token.");
        IReadOnlyList<AccountMembership> memberships = await catalog.GetMembershipsAsync(account.AccountId, ct);
        AccountMembership membership = memberships.FirstOrDefault(m => m.TenantId == stored.TenantId && m.IsActive)
            ?? throw new ForbiddenException("Membership is no longer active.");

        UserProfileWithRole profile = await LoadActiveProfileAsync(membership, ct)
            ?? throw await RejectInactiveAsync(account.Email, account.AccountId, membership.TenantId, client, ct);

        TokenResponse response = await IssueTokensAsync(account, membership, memberships, profile, client, ct);
        await catalog.RevokeRefreshTokenAsync(hash, tokenService.HashRefreshToken(response.RefreshToken), ct);
        await AuditAsync("Refresh", true, account.Email, account.AccountId, membership.TenantId, null, client, ct);
        return response;
    }

    public async Task<TokenResponse> SwitchTenantAsync(SwitchTenantRequest request, ClientInfo client, CancellationToken ct = default)
    {
        int fromTenantId = tenant.TenantId; // captured before LoadActiveProfileAsync rebinds the scope
        string accountId = currentUser.AccountId ?? throw new ForbiddenException();
        Account account = await catalog.GetAccountByIdAsync(accountId, ct) ?? throw new ForbiddenException();
        IReadOnlyList<AccountMembership> memberships = await catalog.GetMembershipsAsync(accountId, ct);
        AccountMembership membership = memberships.FirstOrDefault(m => m.IsActive && m.TenantCode.Equals(request.TenantCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException("Tenant membership", request.TenantCode);

        UserProfileWithRole profile = await LoadActiveProfileAsync(membership, ct)
            ?? throw await RejectInactiveAsync(account.Email, accountId, membership.TenantId, client, ct);

        await AuditAsync("SwitchTenant", true, account.Email, accountId, membership.TenantId, $"from tenant {fromTenantId}", client, ct);
        return await IssueTokensAsync(account, membership, memberships, profile, client, ct);
    }

    public async Task LogoutAsync(string? refreshToken, bool revokeAll, ClientInfo client, CancellationToken ct = default)
    {
        if (revokeAll && currentUser.AccountId is { } id)
        {
            await catalog.RevokeAllRefreshTokensAsync(id, ct);
            await catalog.BumpTokenVersionAsync(id, ct);
            await AuditAsync("LogoutAll", true, currentUser.Email, id, tenant.HasTenant ? tenant.TenantId : null, null, client, ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await catalog.RevokeRefreshTokenAsync(tokenService.HashRefreshToken(refreshToken), null, ct);
            await AuditAsync("Logout", true, currentUser.Email, currentUser.AccountId, tenant.HasTenant ? tenant.TenantId : null, null, client, ct);
        }
    }

    public async Task<AuthenticatedUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        string accountId = currentUser.AccountId ?? throw new ForbiddenException();
        UserProfileWithRole profile = await users.GetByIdAsync(currentUser.UserProfileId, ct)
            ?? throw new NotFoundException("User", currentUser.UserProfileId);
        IReadOnlyList<AccountMembership> memberships = await catalog.GetMembershipsAsync(accountId, ct);
        AccountMembership membership = memberships.FirstOrDefault(m => m.TenantId == tenant.TenantId)
            ?? throw new ForbiddenException("Membership is no longer active.");
        return await BuildUserAsync(accountId, membership, memberships, profile, ct);
    }

    public async Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(CancellationToken ct = default)
    {
        string accountId = currentUser.AccountId ?? throw new ForbiddenException();
        return (await catalog.GetMembershipsAsync(accountId, ct)).Where(m => m.IsActive).Select(ToMembership).ToList();
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, ClientInfo client, CancellationToken ct = default)
    {
        string accountId = currentUser.AccountId ?? throw new ForbiddenException();
        Account account = await catalog.GetAccountByIdAsync(accountId, ct) ?? throw new NotFoundException("Account", accountId);

        if (passwordHasher.Verify(account.PasswordHash ?? string.Empty, currentPassword) == PasswordVerificationResult.Failed)
            throw new BusinessRuleException("Current password is incorrect.");

        string hash = passwordHasher.Hash(newPassword);
        await catalog.UpdatePasswordHashAsync(accountId, hash, currentUser.UserProfileId, byApi: true, ct);

        // Mirror into AspNetUsers of EVERY tenant the account belongs to, so the legacy app accepts the new password
        // everywhere and no stale tenant copy can flow back. From now on the catalog is authoritative for this
        // account (Account.PasswordChangedByApiAtUtc) and AccountSyncJob mirrors catalog -> tenant, never the reverse.
        int originalTenantId = tenant.TenantId;
        string originalTenantCode = tenant.TenantCode;
        foreach (AccountMembership m in await catalog.GetMembershipsAsync(accountId, ct))
        {
            tenantSetter.Set(m.TenantId, m.TenantCode);
            await users.MirrorPasswordHashAsync(accountId, hash, ct);
        }
        tenantSetter.Set(originalTenantId, originalTenantCode);

        await catalog.RevokeAllRefreshTokensAsync(accountId, ct);
        await catalog.BumpTokenVersionAsync(accountId, ct);
        await AuditAsync("PasswordChanged", true, account.Email, accountId, tenant.TenantId, null, client, ct);
    }

    // ---------- helpers ----------

    private static AccountMembership ChooseMembership(IReadOnlyList<AccountMembership> memberships, string? tenantCode)
    {
        AccountMembership[] active = memberships.Where(m => m.IsActive).ToArray();
        if (active.Length == 0)
            throw new ForbiddenException("This account has no active company membership.");

        if (!string.IsNullOrWhiteSpace(tenantCode))
            return active.FirstOrDefault(m => m.TenantCode.Equals(tenantCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new ForbiddenException("This account is not a member of the requested company.");

        if (active.Length == 1) return active[0];
        return active.FirstOrDefault(m => m.IsDefault)
            ?? throw new TenantSelectionRequiredException(active.Select(ToMembership).ToList());
    }

    /// <summary>Binds the scope to the membership's tenant and loads the profile; null when the profile is inactive/deleted.</summary>
    private async Task<UserProfileWithRole?> LoadActiveProfileAsync(AccountMembership membership, CancellationToken ct)
    {
        tenantSetter.Set(membership.TenantId, membership.TenantCode);
        UserProfileWithRole? profile = await users.GetByIdAsync(membership.UserProfileId, ct);
        return profile is { } p && p.StatusId == (int)UserStatus.Active && (p.IsDeleted ?? 0) == 0 ? p : null;
    }

    private async Task<ForbiddenException> RejectInactiveAsync(string? email, string accountId, int tenantId, ClientInfo client, CancellationToken ct)
    {
        await AuditAsync("LoginFailed", false, email, accountId, tenantId, "inactive account", client, ct);
        return new ForbiddenException("Your account is currently inactive.");
    }

    private async Task<TokenResponse> IssueTokensAsync(Account account, AccountMembership membership, IReadOnlyList<AccountMembership> memberships, UserProfileWithRole profile, ClientInfo client, CancellationToken ct)
    {
        AuthenticatedUser user = await BuildUserAsync(account.AccountId, membership, memberships, profile, ct);
        Account fresh = await catalog.GetAccountByIdAsync(account.AccountId, ct) ?? account;
        (string accessToken, DateTime accessExpires) = tokenService.CreateAccessToken(user, fresh.TokenVersion);

        string refreshToken = tokenService.CreateRefreshToken();
        DateTime now = clock.UtcNow;
        var stored = new RefreshToken
        {
            AccountId = account.AccountId,
            TenantId = membership.TenantId,
            UserProfileId = profile.UserProfileId,
            TokenHash = tokenService.HashRefreshToken(refreshToken),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_options.RefreshTokenDays),
            CreatedByIp = client.IpAddress,
            UserAgent = Truncate(client.UserAgent, 400),
        };
        await catalog.SaveRefreshTokenAsync(stored, ct);

        return new TokenResponse(accessToken, accessExpires, refreshToken, stored.ExpiresAtUtc, user);
    }

    private async Task<AuthenticatedUser> BuildUserAsync(string accountId, AccountMembership membership, IReadOnlyList<AccountMembership> memberships, UserProfileWithRole profile, CancellationToken ct)
    {
        IReadOnlyList<EffectiveUserRole> roles = await permissions.GetEffectiveRolesAsync(profile.UserProfileId, ct);
        IReadOnlySet<int> actionIds = await permissions.GetActionIdsAsync(profile.UserProfileId, ct);
        return new AuthenticatedUser(
            accountId,
            membership.TenantId,
            membership.TenantCode,
            profile.UserProfileId,
            profile.Email ?? string.Empty,
            profile.FullName,
            profile.RoleId,
            profile.RoleName,
            profile.UserTypeId,
            LegacyCompanyId,
            profile.DefaultPath,
            profile.RemoteAccessAllowed ?? false,
            roles.Select(r => new EffectiveRole(r.RoleId, r.RoleName, r.IsPrimary, r.ValidFromUtc, r.ValidToUtc)).ToList(),
            actionIds.Order().ToArray(),
            memberships.Where(m => m.IsActive).Select(ToMembership).ToList());
    }

    private static TenantMembership ToMembership(AccountMembership m) =>
        new(m.TenantId, m.TenantCode, m.TenantName, m.UserProfileId, m.IsDefault);

    private Task AuditAsync(string evt, bool success, string? email, string? accountId, int? tenantId, string? detail, ClientInfo client, CancellationToken ct) =>
        catalog.WriteAuthAuditAsync(new AuthAuditEvent(evt, success, email, accountId, tenantId, detail, client), ct);

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
