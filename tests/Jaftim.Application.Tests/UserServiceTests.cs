using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;
using Jaftim.Infrastructure.Security;

namespace Jaftim.Application.Tests;

public sealed class UserServiceTests
{
    private static readonly IdentityCompatiblePasswordHasher Hasher = new();

    [Fact]
    public async Task Create_generates_a_temporary_password_writes_credential_profile_and_membership()
    {
        var users = new UsersFake();
        var catalog = new CatalogFake();
        UserService service = Build(users, catalog);

        UserCreatedResult result = await service.CreateAsync(Request("new@x.com", password: null), CancellationToken.None);

        Assert.NotNull(result.TemporaryPassword);
        Assert.Equal(16, result.TemporaryPassword!.Length);
        Assert.False(result.LinkedExistingAccount);
        Assert.Single(users.AspNetUsersInserted);
        Assert.Equal(PasswordVerificationResult.Success, Hasher.Verify(users.AspNetUsersInserted[0].Hash, result.TemporaryPassword));
        Assert.Single(users.Saved);
        Assert.Equal(0, users.Saved[0].UserProfileId);          // insert branch of UserSave
        Assert.Equal(result.AccountId, users.Saved[0].AspNetUserId);
        Assert.Single(catalog.Ensured);
        Assert.Equal((result.AccountId, 1, result.UserProfileId), catalog.Ensured[0]);
    }

    [Fact]
    public async Task Create_with_an_email_known_to_the_catalog_links_the_existing_login_and_returns_no_password()
    {
        var users = new UsersFake();
        var catalog = new CatalogFake { Existing = new Account { AccountId = "acc-old", Email = "old@x.com", PasswordHash = Hasher.Hash("Their0wn!") } };
        UserService service = Build(users, catalog);

        UserCreatedResult result = await service.CreateAsync(Request("old@x.com", password: null), CancellationToken.None);

        Assert.True(result.LinkedExistingAccount);
        Assert.Null(result.TemporaryPassword);
        Assert.Equal("acc-old", result.AccountId);
        Assert.Equal(catalog.Existing.PasswordHash, users.AspNetUsersInserted[0].Hash); // same hash mirrored into this tenant
    }

    [Fact]
    public async Task Create_rejects_duplicate_email_in_tenant_missing_email_and_customer_role()
    {
        var users = new UsersFake { ExistingEmail = "dup@x.com" };
        UserService service = Build(users, new CatalogFake());

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(Request("dup@x.com"), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(Request(null), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateAsync(Request("c@x.com") with { RoleId = 3 }, CancellationToken.None));
        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() => service.CreateAsync(Request("v@x.com") with { StatusId = 9 }, CancellationToken.None));
    }

    [Fact]
    public async Task Update_keeps_email_and_login_id_and_revokes_sessions_when_deactivated()
    {
        var users = new UsersFake { Detail = new UserDetail { UserProfileId = 7, Email = "keep@x.com", AspNetUserId = "acc7", RoleId = 2, StatusId = 2 } };
        var catalog = new CatalogFake();
        UserService service = Build(users, catalog);

        await service.UpdateAsync(7, Request("changed@x.com") with { StatusId = 1 }, CancellationToken.None);

        Assert.Equal("keep@x.com", users.Saved[0].Email);
        Assert.Equal("acc7", users.Saved[0].AspNetUserId);
        Assert.Equal(1, catalog.Bumps);   // deactivated -> live tokens revoked
    }

    [Fact]
    public async Task Create_saves_the_given_username_or_falls_back_to_the_email()
    {
        var users = new UsersFake();
        UserService service = Build(users, new CatalogFake());

        UserCreatedResult given = await service.CreateAsync(Request("a@x.com") with { Username = "  Azhar1567 " }, CancellationToken.None);
        UserCreatedResult defaulted = await service.CreateAsync(Request("b@x.com"), CancellationToken.None);

        Assert.Equal("Azhar1567", users.Saved[0].Username);   // trimmed
        Assert.Equal("b@x.com", users.Saved[1].Username);     // legacy default: username = e-mail
        Assert.Equal(new[] { "Azhar1567", "b@x.com" }, users.UsernameChecks);
        Assert.Equal("Azhar1567", given.Username);            // the response carries what was stored
        Assert.Equal("b@x.com", defaulted.Username);
    }

    [Fact]
    public async Task A_username_used_by_someone_else_is_a_conflict_and_nothing_is_written()
    {
        var users = new UsersFake { Detail = new UserDetail { UserProfileId = 7, Username = "Mine", Email = "m@x.com", RoleId = 2, StatusId = 2 } };
        users.TakenUsernames.Add("saad");
        var catalog = new CatalogFake();
        UserService service = Build(users, catalog);

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(Request("n@x.com") with { Username = "Saad" }, CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateAsync(7, Request(null) with { Username = "SAAD" }, CancellationToken.None));
        Assert.Empty(users.Saved);
        Assert.Empty(users.AspNetUsersInserted);
        Assert.Empty(catalog.Ensured);
    }

    [Fact]
    public async Task Update_changes_the_username_or_keeps_it_when_omitted_and_skips_the_check_when_unchanged()
    {
        var users = new UsersFake { Detail = new UserDetail { UserProfileId = 7, Username = "Saad", Email = "s@x.com", RoleId = 2, StatusId = 2 } };
        users.TakenUsernames.Add("saad");   // a legacy duplicate: re-saving it unchanged must still work
        UserService service = Build(users, new CatalogFake());

        UserDetail kept = await service.UpdateAsync(7, Request(null), CancellationToken.None);
        await service.UpdateAsync(7, Request(null) with { Username = "saad" }, CancellationToken.None);
        UserDetail renamed = await service.UpdateAsync(7, Request(null) with { Username = "Saad Khan" }, CancellationToken.None);

        Assert.Equal(new[] { "Saad", "saad", "Saad Khan" }, users.Saved.Select(s => s.Username));
        Assert.Equal(new[] { "Saad Khan" }, users.UsernameChecks);
        Assert.Equal("Saad", kept.Username);                  // the response is the user as stored after the save
        Assert.Equal("Saad Khan", renamed.Username);
    }

    [Fact]
    public async Task A_blank_username_is_rejected_instead_of_silently_kept()
    {
        UserService service = Build(new UsersFake(), new CatalogFake());
        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() =>
            service.CreateAsync(Request("v@x.com") with { Username = "   " }, CancellationToken.None));
    }

    [Fact]
    public async Task Toggle_refuses_self_deactivation()
    {
        var users = new UsersFake { Detail = new UserDetail { UserProfileId = 500, StatusId = 2, RoleId = 2 } };
        UserService service = Build(users, new CatalogFake());
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.ToggleActiveAsync(500, CancellationToken.None));
    }

    // ---------- helpers ----------

    private static UserUpsertRequest Request(string? email, string? password = null) =>
        new("Test User", email, password, RoleId: 2, GenderId: 1, StatusId: 2);

    private static UserService Build(UsersFake users, CatalogFake catalog) =>
        new(users, catalog, Hasher, new PermissionsFake(), new AuditFake(), new Actor(), new StaticTenant(1), new UserUpsertRequestValidator());

    private sealed class PermissionsFake : IPermissionService
    {
        public Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveRolesAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EffectiveUserRole>>([]);
        public Task<IReadOnlySet<int>> GetActionIdsAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        public Task<bool> HasPermissionAsync(long userProfileId, int actionId, CancellationToken ct = default) => Task.FromResult(true);
        public void InvalidateRole(long roleId) { }
        public void InvalidateUser(long userProfileId) { }
    }

    private sealed class AuditFake : IAuditWriter
    {
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class UsersFake : IUserRepository
    {
        public string? ExistingEmail { get; init; }
        public UserDetail? Detail { get; set; }
        public List<(string Id, string Email, string Hash)> AspNetUsersInserted { get; } = [];
        public List<UserSaveArgs> Saved { get; } = [];

        public Task<UserProfileWithRole?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(email == ExistingEmail ? new UserProfileWithRole { UserProfileId = 1 } : null);
        public Task<UserProfileWithRole?> GetByIdAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<UserProfileWithRole?>(null);
        public Task<UserProfileWithRole?> GetByAspNetUserIdAsync(string aspNetUserId, CancellationToken ct = default) =>
            Task.FromResult<UserProfileWithRole?>(new UserProfileWithRole { UserProfileId = 123, AspNetUserId = aspNetUserId, RoleId = 2, Username = Saved.LastOrDefault()?.Username });
        public Task<IReadOnlyList<RoleAction>> GetRoleActionsAsync(long roleId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleAction>>([]);
        public Task<IReadOnlyList<string>> GetWhitelistedCidrsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SaveActionUrlAsync(string actionUrl, long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RoleAction>> GetAllRoleActionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleAction>>([]);
        public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Role>>([new Role { RoleId = 2, RoleName = "Sales Executive" }, new Role { RoleId = 3, RoleName = "Customer" }]);
        public Task MirrorPasswordHashAsync(string aspNetUserId, string passwordHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TenantCredentialRow>> GetCredentialRowsForSyncAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TenantCredentialRow>>([]);
        public Task<Jaftim.Application.Common.PagedResult<UserListItem>> GetAllAsync(UserListRequest request, CancellationToken ct = default) => Task.FromResult(Jaftim.Application.Common.PagedResult<UserListItem>.Empty(request));
        public Task<UserDetail?> GetDetailAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult(Detail);
        public Task SaveAsync(UserSaveArgs args, CancellationToken ct = default)
        {
            Saved.Add(args);
            if (Detail is not null && args.UserProfileId == Detail.UserProfileId)
                Detail = new UserDetail { UserProfileId = Detail.UserProfileId, Username = args.Username, Email = Detail.Email, AspNetUserId = Detail.AspNetUserId, RoleId = args.RoleId, StatusId = args.StatusId };
            return Task.CompletedTask;
        }
        public Task ToggleActiveAsync(long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertAspNetUserAsync(string id, string email, string passwordHash, CancellationToken ct = default) { AspNetUsersInserted.Add((id, email, passwordHash)); return Task.CompletedTask; }
        public HashSet<string> TakenUsernames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> UsernameChecks { get; } = [];
        public Task<bool> UsernameTakenAsync(string username, long exceptUserProfileId, CancellationToken ct = default)
        {
            UsernameChecks.Add(username);
            return Task.FromResult(TakenUsernames.Contains(username));
        }
    }

    private sealed class CatalogFake : IAuthRepository
    {
        public Account? Existing { get; init; }
        public List<(string AccountId, int TenantId, long UserProfileId)> Ensured { get; } = [];
        public int Bumps { get; private set; }

        public Task<Account?> GetAccountByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(Existing?.Email == email ? Existing : null);
        public Task<Account?> GetAccountByIdAsync(string accountId, CancellationToken ct = default) => Task.FromResult<Account?>(null);
        public Task UpdatePasswordHashAsync(string accountId, string passwordHash, long? actorUserProfileId, bool byApi, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordFailedAccessAsync(string accountId, int maxFailedAttempts, TimeSpan lockoutDuration, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetFailedAccessAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> BumpTokenVersionAsync(string accountId, CancellationToken ct = default) { Bumps++; return Task.FromResult(2); }
        public Task<IReadOnlyList<AccountMembership>> GetMembershipsAsync(string accountId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountMembership>>([]);
        public Task<string> EnsureAccountAsync(string accountId, string email, string passwordHash, int tenantId, long userProfileId, bool isActive, CancellationToken ct = default)
        { Ensured.Add((accountId, tenantId, userProfileId)); return Task.FromResult(accountId); }
        public Task SaveRefreshTokenAsync(RefreshToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default) => Task.FromResult<RefreshToken?>(null);
        public Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevokeAllRefreshTokensAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAuthAuditAsync(AuthAuditEvent entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
