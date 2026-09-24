using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;
using Jaftim.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Jaftim.Application.Tests;

public sealed class CachedPermissionServiceTests
{
    [Fact]
    public async Task Permissions_are_the_union_of_effective_roles_and_expired_or_future_roles_are_excluded()
    {
        // The repository fake applies the validity window the SQL function applies.
        var roles = new FakeUserRoles();
        roles.Rows[1] =
        [
            new UserRoleAssignment { RoleId = 2, IsPrimary = true, IsActive = true },
            new UserRoleAssignment { RoleId = 13, IsActive = true, ValidToUtc = Clock.Now.AddHours(1) },      // active, temporary
            new UserRoleAssignment { RoleId = 4, IsActive = true, ValidToUtc = Clock.Now.AddHours(-1) },      // expired
            new UserRoleAssignment { RoleId = 5, IsActive = true, ValidFromUtc = Clock.Now.AddDays(1) },      // future
        ];
        var users = new FakeUsers { RoleActions = { [2] = [417], [13] = [612, 613], [4] = [999], [5] = [998] } };
        var service = new CachedPermissionService(users, roles, new StaticTenant(1), new Clock(), new MemoryCache(new MemoryCacheOptions()));

        IReadOnlySet<int> actions = await service.GetActionIdsAsync(1);

        Assert.Equal(new HashSet<int> { 417, 612, 613 }, actions);
        Assert.True(await service.HasPermissionAsync(1, 613));
        Assert.False(await service.HasPermissionAsync(1, 999));
    }

    [Fact]
    public async Task Invalidating_the_user_refreshes_roles_immediately()
    {
        var roles = new FakeUserRoles();
        roles.Rows[1] = [new UserRoleAssignment { RoleId = 2, IsPrimary = true, IsActive = true }];
        var users = new FakeUsers { RoleActions = { [2] = [417], [13] = [612] } };
        var service = new CachedPermissionService(users, roles, new StaticTenant(1), new Clock(), new MemoryCache(new MemoryCacheOptions()));

        Assert.False(await service.HasPermissionAsync(1, 612));
        roles.Rows[1].Add(new UserRoleAssignment { RoleId = 13, IsActive = true });

        Assert.False(await service.HasPermissionAsync(1, 612));   // still cached
        service.InvalidateUser(1);
        Assert.True(await service.HasPermissionAsync(1, 612));
    }

    [Fact]
    public async Task Cache_keys_are_per_tenant()
    {
        var roles = new FakeUserRoles();
        roles.Rows[1] = [new UserRoleAssignment { RoleId = 2, IsPrimary = true, IsActive = true }];
        var users = new FakeUsers { RoleActions = { [2] = [417] } };
        var cache = new MemoryCache(new MemoryCacheOptions());

        Assert.True(await new CachedPermissionService(users, roles, new StaticTenant(1), new Clock(), cache).HasPermissionAsync(1, 417));
        users.RoleActions[2] = [];
        // A different tenant must not see tenant 1's cached role set.
        Assert.False(await new CachedPermissionService(users, roles, new StaticTenant(2), new Clock(), cache).HasPermissionAsync(1, 417));
    }
}

public sealed class UserRoleServiceTests
{
    [Fact]
    public async Task Assign_rejects_customer_role_for_staff_and_time_boxing_the_primary_role()
    {
        UserRoleService service = Build(out _, out _);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.AssignAsync(1, new AssignRoleRequest(3)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.AssignAsync(1, new AssignRoleRequest(2, ValidToUtc: DateTime.UtcNow.AddDays(1))));
    }

    [Fact]
    public async Task Assign_validates_dates_and_role_existence()
    {
        UserRoleService service = Build(out _, out _);

        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() => service.AssignAsync(1, new AssignRoleRequest(13, DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(1))));
        await Assert.ThrowsAsync<NotFoundException>(() => service.AssignAsync(1, new AssignRoleRequest(9999)));
    }

    [Fact]
    public async Task Assign_invalidates_permissions_and_audits()
    {
        UserRoleService service = Build(out FakePermissions permissions, out FakeAudit audit);

        var result = await service.AssignAsync(1, new AssignRoleRequest(13, ValidToUtc: DateTime.UtcNow.AddDays(7), Reason: "cover"));

        Assert.Contains(result, r => r.RoleId == 13);
        Assert.Equal([1L], permissions.InvalidatedUsers);
        Assert.Single(audit.Records, a => a.Action == "UserRole.Assigned");
    }

    [Fact]
    public async Task Revoking_the_primary_role_is_refused()
    {
        UserRoleService service = Build(out _, out _);
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.RevokeAsync(1, 2));
    }

    private static UserRoleService Build(out FakePermissions permissions, out FakeAudit audit)
    {
        var roles = new FakeUserRoles();
        roles.Rows[1] = [new UserRoleAssignment { RoleId = 2, RoleName = "Sales Executive", IsPrimary = true, IsActive = true }];
        var users = new FakeUsers
        {
            Roles = [new Role { RoleId = 2, RoleName = "Sales Executive" }, new Role { RoleId = 3, RoleName = "Customer" }, new Role { RoleId = 13, RoleName = "Finance Manager" }],
        };
        permissions = new FakePermissions();
        audit = new FakeAudit();
        return new UserRoleService(roles, users, permissions, audit, new Actor(), new AssignRoleRequestValidator());
    }
}

// ---------- shared fakes ----------

internal sealed class Clock : IDateTimeProvider
{
    public static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow => Now;
}

internal sealed class StaticTenant(int id) : ITenantContext
{
    public bool HasTenant => true; public int TenantId => id; public string TenantCode => $"t{id}";
}

internal sealed class Actor : ICurrentUser
{
    public bool IsAuthenticated => true; public long UserProfileId => 500; public string? AccountId => "admin"; public string? Email => "admin@x";
    public string? FullName => "Admin"; public long RoleId => 1; public int UserTypeId => 1; public int CompanyId => 1;
}

internal sealed class FakePermissions : IPermissionService
{
    public List<long> InvalidatedUsers { get; } = [];
    public Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveRolesAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EffectiveUserRole>>([]);
    public Task<IReadOnlySet<int>> GetActionIdsAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    public Task<bool> HasPermissionAsync(long userProfileId, int actionId, CancellationToken ct = default) => Task.FromResult(false);
    public void InvalidateRole(long roleId) { }
    public void InvalidateUser(long userProfileId) => InvalidatedUsers.Add(userProfileId);
}

internal sealed class FakeAudit : IAuditWriter
{
    public List<(string Action, string EntityType, long? EntityId)> Records { get; } = [];
    public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
    {
        Records.Add((action, entityType, entityId));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeUserRoles : IUserRoleRepository
{
    public Dictionary<long, List<UserRoleAssignment>> Rows { get; } = [];

    public Task<IReadOnlyList<UserRoleAssignment>> GetByUserAsync(long userProfileId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserRoleAssignment>>(Rows.GetValueOrDefault(userProfileId) ?? []);

    public Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveAsync(long userProfileId, DateTime asOfUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EffectiveUserRole>>((Rows.GetValueOrDefault(userProfileId) ?? [])
            .Where(r => r.IsPrimary || (r.IsActive && (r.ValidFromUtc is null || r.ValidFromUtc <= asOfUtc) && (r.ValidToUtc is null || r.ValidToUtc > asOfUtc)))
            .Select(r => new EffectiveUserRole { RoleId = r.RoleId, RoleName = r.RoleName, IsPrimary = r.IsPrimary, ValidFromUtc = r.ValidFromUtc, ValidToUtc = r.ValidToUtc })
            .ToList());

    public Task<long> SaveAsync(long userProfileId, long roleId, DateTime? validFromUtc, DateTime? validToUtc, string? reason, long actorUserProfileId, CancellationToken ct = default)
    {
        List<UserRoleAssignment> list = Rows.GetValueOrDefault(userProfileId) ?? (Rows[userProfileId] = []);
        list.RemoveAll(r => r.RoleId == roleId);
        list.Add(new UserRoleAssignment { RoleId = roleId, ValidFromUtc = validFromUtc, ValidToUtc = validToUtc, Reason = reason, IsActive = true });
        return Task.FromResult(1L);
    }

    public Task<int> RevokeAsync(long userProfileId, long roleId, long actorUserProfileId, CancellationToken ct = default) =>
        Task.FromResult(Rows.GetValueOrDefault(userProfileId)?.RemoveAll(r => r.RoleId == roleId) ?? 0);

    public Task SetPrimaryAsync(long userProfileId, long roleId, long actorUserProfileId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeUsers : IUserRepository
{
    public Dictionary<long, int[]> RoleActions { get; } = [];
    public List<Role> Roles { get; init; } = [];

    public Task<UserProfileWithRole?> GetByIdAsync(long userProfileId, CancellationToken ct = default) =>
        Task.FromResult<UserProfileWithRole?>(new UserProfileWithRole { UserProfileId = userProfileId, RoleId = 2, RoleName = "Sales Executive", StatusId = 2, IsDeleted = 0 });
    public Task<UserProfileWithRole?> GetByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<UserProfileWithRole?>(null);
    public Task<UserProfileWithRole?> GetByAspNetUserIdAsync(string aspNetUserId, CancellationToken ct = default) => Task.FromResult<UserProfileWithRole?>(null);
    public Task<IReadOnlyList<RoleAction>> GetRoleActionsAsync(long roleId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RoleAction>>((RoleActions.GetValueOrDefault(roleId) ?? []).Select(id => new RoleAction { ActionId = id }).ToList());
    public Task<IReadOnlyList<string>> GetWhitelistedCidrsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task SaveActionUrlAsync(string actionUrl, long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<RoleAction>> GetAllRoleActionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RoleAction>>([]);
    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Role>>(Roles);
    public Task MirrorPasswordHashAsync(string aspNetUserId, string passwordHash, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<TenantCredentialRow>> GetCredentialRowsForSyncAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TenantCredentialRow>>([]);
    public Task<IReadOnlyList<UserListItem>> GetAllAsync(int? userTypeId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserListItem>>([]);
    public Task<UserDetail?> GetDetailAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<UserDetail?>(null);
    public Task SaveAsync(UserSaveArgs args, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToggleActiveAsync(long userProfileId, CancellationToken ct = default) => Task.CompletedTask;
    public Task InsertAspNetUserAsync(string id, string email, string passwordHash, CancellationToken ct = default) => Task.CompletedTask;
}
