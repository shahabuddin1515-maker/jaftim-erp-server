using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Enums;
using Jaftim.Domain.Exceptions;
using Jaftim.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Jaftim.Application.Tests;

/// <summary>
/// Hand-rolled fakes covering the login rules carried over from the legacy Login page plus the tenant rules:
/// wrong password -> failure audited; correct password but inactive profile -> rejected; several memberships without
/// a choice -> TenantSelectionRequired; switch-tenant issues a token bound to the other tenant; replay revokes all.
/// </summary>
public sealed class AuthServiceTests
{
    private static readonly IdentityCompatiblePasswordHasher Hasher = new();
    private const string Password = "Correct1!";

    [Fact]
    public async Task Login_with_wrong_password_records_failure_and_throws()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 1, "jaftim", 42, isDefault: true);
        AuthService service = Build(catalog, new FakeUsers { Profile = Profile(active: true) });

        await Assert.ThrowsAsync<ForbiddenException>(() => service.LoginAsync(new LoginRequest("a@b.com", "wrong"), Client, CancellationToken.None));

        Assert.Equal(1, catalog.FailedAccessRecorded);
        Assert.Contains(catalog.Audit, a => a.Event == "LoginFailed");
    }

    [Fact]
    public async Task Login_with_inactive_profile_is_rejected_after_password_check()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 1, "jaftim", 42, isDefault: true);
        AuthService service = Build(catalog, new FakeUsers { Profile = Profile(active: false) });

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => service.LoginAsync(new LoginRequest("a@b.com", Password), Client, CancellationToken.None));

        Assert.Contains("inactive", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(catalog.RefreshTokens);
    }

    [Fact]
    public async Task Single_membership_logs_in_without_tenant_code_and_binds_the_tenant()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 7, "acme", 42);
        var tenantCtx = new FakeTenantContext();
        AuthService service = Build(catalog, new FakeUsers { Profile = Profile(active: true) }, tenantCtx: tenantCtx);

        TokenResponse response = await service.LoginAsync(new LoginRequest("a@b.com", Password), Client, CancellationToken.None);

        Assert.Equal(7, response.User.TenantId);
        Assert.Equal("acme", response.User.TenantCode);
        Assert.Equal(7, tenantCtx.TenantId);
        Assert.Equal([101, 417], response.User.Permissions);
        Assert.Single(response.User.Memberships);
        Assert.Single(catalog.RefreshTokens);
        Assert.Equal(7, catalog.RefreshTokens[0].TenantId);
        Assert.Contains(catalog.Audit, a => a is { Event: "Login", Success: true });
    }

    [Fact]
    public async Task Multiple_memberships_without_default_require_a_tenant_choice()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 1, "jaftim", 42).WithMembership("acc1", 2, "acme", 99);
        AuthService service = Build(catalog, new FakeUsers { Profile = Profile(active: true) });

        var ex = await Assert.ThrowsAsync<TenantSelectionRequiredException>(() => service.LoginAsync(new LoginRequest("a@b.com", Password), Client, CancellationToken.None));
        Assert.Equal(2, ex.Memberships.Count);

        TokenResponse chosen = await service.LoginAsync(new LoginRequest("a@b.com", Password, "acme"), Client, CancellationToken.None);
        Assert.Equal("acme", chosen.User.TenantCode);
        Assert.Equal(99, chosen.User.UserProfileId);
    }

    [Fact]
    public async Task Switch_tenant_issues_tokens_for_the_other_membership_only()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 1, "jaftim", 42).WithMembership("acc1", 2, "acme", 99);
        AuthService service = Build(catalog, new FakeUsers { Profile = Profile(active: true) }, currentUser: new SignedIn("acc1", 42));

        TokenResponse switched = await service.SwitchTenantAsync(new SwitchTenantRequest("acme"), Client, CancellationToken.None);
        Assert.Equal(2, switched.User.TenantId);
        Assert.Equal(99, switched.User.UserProfileId);

        await Assert.ThrowsAsync<NotFoundException>(() => service.SwitchTenantAsync(new SwitchTenantRequest("nope"), Client, CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_with_revoked_token_revokes_every_session_and_bumps_token_version()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 1, "jaftim", 42);
        catalog.RefreshTokens.Add(new RefreshToken { AccountId = "acc1", TenantId = 1, TokenHash = "HASH", ExpiresAtUtc = DateTime.UtcNow.AddDays(1), RevokedAtUtc = DateTime.UtcNow });
        AuthService service = Build(catalog, new FakeUsers { Profile = Profile(active: true) }, tokens: new StaticHashTokenService("HASH"));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.RefreshAsync(new RefreshTokenRequest("raw"), Client, CancellationToken.None));

        Assert.Equal(1, catalog.RevokeAllCalls);
        Assert.Equal(1, catalog.BumpCalls);
        Assert.Contains(catalog.Audit, a => a.Event == "RefreshReplay");
    }

    [Fact]
    public async Task Change_password_mirrors_into_every_tenant_and_marks_catalog_authoritative()
    {
        var catalog = new FakeCatalog().WithAccount("acc1").WithMembership("acc1", 1, "jaftim", 42).WithMembership("acc1", 2, "acme", 99);
        var users = new FakeUsers { Profile = Profile(active: true) };
        AuthService service = Build(catalog, users, currentUser: new SignedIn("acc1", 42));

        await service.ChangePasswordAsync(Password, "NewPass1!", Client, CancellationToken.None);

        Assert.True(catalog.LastPasswordByApi);
        Assert.Equal(2, users.MirroredTenants.Count);
        Assert.Equal(1, catalog.BumpCalls);
    }

    // ---------- helpers ----------

    private static readonly ClientInfo Client = new("127.0.0.1", "xunit");

    private static AuthService Build(FakeCatalog catalog, FakeUsers users, ITokenService? tokens = null, ICurrentUser? currentUser = null, FakeTenantContext? tenantCtx = null)
    {
        tenantCtx ??= new FakeTenantContext();
        users.Tenant = tenantCtx;
        return new AuthService(catalog, users, new FakePermissions(), Hasher, tokens ?? new JwtTokenService(Options(), new FixedClock()),
            currentUser ?? new NoUser(), tenantCtx, tenantCtx, new FixedClock(), Options());
    }

    private static IOptions<AuthOptions> Options() => Microsoft.Extensions.Options.Options.Create(new AuthOptions
    {
        SigningKey = "unit-test-signing-key-at-least-32-bytes-long!!",
    });

    private static UserProfileWithRole Profile(bool active) => new()
    {
        UserProfileId = 42, AspNetUserId = "acc1", Email = "a@b.com", FullName = "Test", RoleId = 2, RoleName = "Sales Executive",
        UserTypeId = 2, StatusId = active ? (int)UserStatus.Active : (int)UserStatus.Inactive, IsDeleted = 0,
    };

    private sealed class FixedClock : IDateTimeProvider { public DateTime UtcNow => new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc); }

    private sealed class FakeTenantContext : ITenantContext, ITenantContextSetter
    {
        public bool HasTenant => TenantId > 0; public int TenantId { get; private set; } public string TenantCode { get; private set; } = string.Empty;
        public void Set(int tenantId, string tenantCode) { TenantId = tenantId; TenantCode = tenantCode; }
    }

    private sealed class NoUser : ICurrentUser
    {
        public bool IsAuthenticated => false; public long UserProfileId => 0; public string? AccountId => null; public string? Email => null;
        public string? FullName => null; public long RoleId => 0; public int UserTypeId => 0; public int CompanyId => 1;
    }

    private sealed class SignedIn(string accountId, long userProfileId) : ICurrentUser
    {
        public bool IsAuthenticated => true; public long UserProfileId => userProfileId; public string? AccountId => accountId; public string? Email => "a@b.com";
        public string? FullName => "Test"; public long RoleId => 2; public int UserTypeId => 2; public int CompanyId => 1;
    }

    private sealed class FakePermissions : IPermissionService
    {
        public Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveRolesAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EffectiveUserRole>>([new EffectiveUserRole { RoleId = 2, RoleName = "Sales Executive", IsPrimary = true }]);
        public Task<IReadOnlySet<int>> GetActionIdsAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int> { 417, 101 });
        public Task<bool> HasPermissionAsync(long userProfileId, int actionId, CancellationToken ct = default) => Task.FromResult(actionId is 101 or 417);
        public void InvalidateRole(long roleId) { }
        public void InvalidateUser(long userProfileId) { }
    }

    private sealed class StaticHashTokenService(string hash) : ITokenService
    {
        public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AuthenticatedUser user, int tokenVersion) => ("t", DateTime.UtcNow.AddMinutes(5));
        public string CreateRefreshToken() => "raw";
        public string HashRefreshToken(string refreshToken) => hash;
    }

    private sealed class FakeUsers : IUserRepository
    {
        public UserProfileWithRole? Profile { get; init; }
        public FakeTenantContext Tenant { get; set; } = new();
        public List<int> MirroredTenants { get; } = [];

        // The profile id depends on the membership: 99 in tenant 2, 42 elsewhere - mimics two databases.
        public Task<UserProfileWithRole?> GetByIdAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult(Profile is null ? null : new UserProfileWithRole
            {
                UserProfileId = userProfileId, AspNetUserId = Profile.AspNetUserId, Email = Profile.Email, FullName = Profile.FullName, RoleId = Profile.RoleId,
                RoleName = Profile.RoleName, UserTypeId = Profile.UserTypeId, StatusId = Profile.StatusId, IsDeleted = Profile.IsDeleted,
            })!;
        public Task<UserProfileWithRole?> GetByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(Profile);
        public Task<UserProfileWithRole?> GetByAspNetUserIdAsync(string aspNetUserId, CancellationToken ct = default) => Task.FromResult(Profile);
        public Task<IReadOnlyList<RoleAction>> GetRoleActionsAsync(long roleId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleAction>>([]);
        public Task<IReadOnlyList<string>> GetWhitelistedCidrsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SaveActionUrlAsync(string actionUrl, long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RoleAction>> GetAllRoleActionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleAction>>([]);
    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Role>>([]);
        public Task MirrorPasswordHashAsync(string aspNetUserId, string passwordHash, CancellationToken ct = default) { MirroredTenants.Add(Tenant.TenantId); return Task.CompletedTask; }
        public Task<IReadOnlyList<TenantCredentialRow>> GetCredentialRowsForSyncAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TenantCredentialRow>>([]);
    public Task<IReadOnlyList<UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserListItem>>([]);
    public Task<UserDetail?> GetDetailAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<UserDetail?>(null);
    public Task SaveAsync(UserSaveArgs args, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToggleActiveAsync(long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
    public Task InsertAspNetUserAsync(string id, string email, string passwordHash, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeCatalog : IAuthRepository
    {
        private readonly Dictionary<string, Account> _accounts = [];
        private readonly List<AccountMembership> _memberships = [];
        public List<RefreshToken> RefreshTokens { get; } = [];
        public List<AuthAuditEvent> Audit { get; } = [];
        public int FailedAccessRecorded { get; private set; }
        public int RevokeAllCalls { get; private set; }
        public int BumpCalls { get; private set; }
        public bool LastPasswordByApi { get; private set; }

        public FakeCatalog WithAccount(string id)
        {
            _accounts[id] = new Account { AccountId = id, Email = "a@b.com", NormalizedEmail = "A@B.COM", PasswordHash = Hasher.Hash(Password), LockoutEnabled = true, TokenVersion = 1, IsActive = true };
            return this;
        }

        public FakeCatalog WithMembership(string accountId, int tenantId, string code, long userProfileId, bool isDefault = false)
        {
            _memberships.Add(new AccountMembership { AccountId = accountId, TenantId = tenantId, TenantCode = code, TenantName = code, UserProfileId = userProfileId, IsDefault = isDefault, IsActive = true });
            return this;
        }

        public Task<Account?> GetAccountByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(_accounts.Values.FirstOrDefault(a => a.Email == email));
        public Task<Account?> GetAccountByIdAsync(string accountId, CancellationToken ct = default) => Task.FromResult(_accounts.GetValueOrDefault(accountId));
        public Task UpdatePasswordHashAsync(string accountId, string passwordHash, long? actorUserProfileId, bool byApi, CancellationToken ct = default) { LastPasswordByApi = byApi; return Task.CompletedTask; }
        public Task RecordFailedAccessAsync(string accountId, int maxFailedAttempts, TimeSpan lockoutDuration, CancellationToken ct = default) { FailedAccessRecorded++; return Task.CompletedTask; }
        public Task ResetFailedAccessAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> BumpTokenVersionAsync(string accountId, CancellationToken ct = default) { BumpCalls++; return Task.FromResult(2); }
        public Task<IReadOnlyList<AccountMembership>> GetMembershipsAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AccountMembership>>(_memberships.Where(m => m.AccountId == accountId).ToList());
        public Task SaveRefreshTokenAsync(RefreshToken token, CancellationToken ct = default) { RefreshTokens.Add(token); return Task.CompletedTask; }
        public Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default) => Task.FromResult(RefreshTokens.FirstOrDefault(t => t.TokenHash == tokenHash));
        public Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevokeAllRefreshTokensAsync(string accountId, CancellationToken ct = default) { RevokeAllCalls++; return Task.CompletedTask; }
        public Task WriteAuthAuditAsync(AuthAuditEvent entry, CancellationToken ct = default) { Audit.Add(entry); return Task.CompletedTask; }
        public Task<string> EnsureAccountAsync(string accountId, string email, string passwordHash, int tenantId, long userProfileId, bool isActive, CancellationToken ct = default) => Task.FromResult(accountId);
    }
}
