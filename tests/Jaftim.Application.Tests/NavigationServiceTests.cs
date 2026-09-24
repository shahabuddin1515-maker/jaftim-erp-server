using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Navigation;
using Jaftim.Domain.Entities.Navigation;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class NavigationServiceTests
{
    private static readonly NavigationItem[] Tree =
    [
        new() { NavigationItemId = 1, Code = "stock", Title = "Stock", SortOrder = 10, ActionId = 400, IsActive = true },
        new() { NavigationItemId = 2, ParentId = 1, Code = "stock.list", Title = "Stocks", Route = "/stocks", SortOrder = 10, ActionId = 417, IsActive = true },
        new() { NavigationItemId = 3, ParentId = 1, Code = "stock.pricing", Title = "Pricing", Route = "/pricing", SortOrder = 20, ActionId = 577, IsActive = true },
        new() { NavigationItemId = 4, Code = "operations", Title = "Operations", SortOrder = 20, ActionId = 900, IsActive = true },
        new() { NavigationItemId = 5, ParentId = 4, Code = "operations.vendors", Title = "Vendors", Route = "/vendors", SortOrder = 10, ActionId = 605, IsActive = true },
        new() { NavigationItemId = 6, Code = "settings", Title = "Settings", SortOrder = 90, ActionId = 903, RequiredRoleIdsCsv = "1,10", IsActive = true },
        new() { NavigationItemId = 7, ParentId = 6, Code = "settings.nav", Title = "Navigation", Route = "/settings/navigation", SortOrder = 10, ActionId = 300, RequiredRoleIdsCsv = "1", IsActive = true },
        new() { NavigationItemId = 8, ParentId = 1, Code = "stock.hidden", Title = "Hidden", Route = "/x", SortOrder = 30, ActionId = 417, IsActive = false },
    ];

    [Fact]
    public async Task Tree_is_filtered_by_permissions_and_groups_without_visible_children_disappear()
    {
        // Sales executive: Stock module (400) + Stocks (417); no Pricing, no Vendors, not an admin role.
        NavigationService service = Build(actions: [400, 417], roles: [2]);

        NavigationResponse nav = await service.GetForCurrentUserAsync();

        Assert.Single(nav.Modules);
        Assert.Equal("stock", nav.Modules[0].Code);
        Assert.Equal(["stock.list"], nav.Modules[0].Children.Select(c => c.Code));   // pricing gated out, inactive item gone
        Assert.Equal("/stocks", nav.HomeRoute);
    }

    [Fact]
    public async Task Role_gate_and_permission_gate_both_apply()
    {
        // Super Admin (1) with permission 300 sees Settings; System Admin (10) passes the group gate but not the leaf role list.
        NavigationResponse super = await Build(actions: [903, 300], roles: [1]).GetForCurrentUserAsync();
        Assert.Contains(super.Modules, m => m.Code == "settings" && m.Children.Single().Code == "settings.nav");

        NavigationResponse sysAdmin = await Build(actions: [903, 300], roles: [10]).GetForCurrentUserAsync();
        Assert.DoesNotContain(sysAdmin.Modules, m => m.Code == "settings");
    }

    [Fact]
    public async Task Group_shows_when_its_permission_and_any_child_pass()
    {
        Assert.Empty((await Build(actions: [605], roles: [2]).GetForCurrentUserAsync()).Modules);           // child ok, module permission missing
        NavigationResponse nav = await Build(actions: [900, 605], roles: [2]).GetForCurrentUserAsync();
        Assert.Equal(["operations"], nav.Modules.Select(m => m.Code));
        Assert.Equal("/vendors", nav.HomeRoute);
    }

    [Fact]
    public async Task Upsert_rejects_duplicate_codes_unknown_parents_cycles_and_bad_input()
    {
        NavigationService service = Build(actions: [300], roles: [1]);

        await Assert.ThrowsAsync<ConflictException>(() => service.UpsertAsync(new NavigationItemUpsert(null, null, "stock", "Dup", null, null, 0, 400, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpsertAsync(new NavigationItemUpsert(null, "nope", "x.y", "X", null, "/x", 0, 400, null)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.UpsertAsync(new NavigationItemUpsert(1, "stock.list", "stock", "Stock", null, null, 0, 400, null)));
        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() => service.UpsertAsync(new NavigationItemUpsert(null, null, "Bad Code", "X", null, "x", 0, null, null)));
    }

    private static NavigationService Build(int[] actions, long[] roles) =>
        new(new FakeRepo(Tree), new NoCatalog(), new FakePerms(actions, roles), new Actor(), new NoAudit(), new NavigationItemUpsertValidator());

    private sealed class NoCatalog : Jaftim.Application.Modules.Users.IPermissionCatalogService
    {
        public Task<IReadOnlyList<Jaftim.Application.Modules.Users.PermissionNode>> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Jaftim.Application.Modules.Users.PermissionNode>>([]);
        public Task<Jaftim.Application.Modules.Users.PermissionNode> UpsertAsync(Jaftim.Application.Modules.Users.PermissionUpsert request, CancellationToken ct = default) =>
            Task.FromResult(new Jaftim.Application.Modules.Users.PermissionNode(950, request.Name, "_CSS_950", null, []));
    }

    private sealed class FakeRepo(NavigationItem[] items) : INavigationRepository
    {
        public Task<IReadOnlyList<NavigationItem>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NavigationItem>>(items);
        public Task<int> SaveAsync(NavigationItem item, long actorUserProfileId, CancellationToken ct = default) => Task.FromResult(99);
        public Task<int> DeleteAsync(int navigationItemId, long actorUserProfileId, CancellationToken ct = default) => Task.FromResult(1);
    }

    private sealed class FakePerms(int[] actions, long[] roles) : IPermissionService
    {
        public Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveRolesAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EffectiveUserRole>>(roles.Select(r => new EffectiveUserRole { RoleId = r }).ToList());
        public Task<IReadOnlySet<int>> GetActionIdsAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<int>>(actions.ToHashSet());
        public Task<bool> HasPermissionAsync(long userProfileId, int actionId, CancellationToken ct = default) => Task.FromResult(actions.Contains(actionId));
        public void InvalidateRole(long roleId) { }
        public void InvalidateUser(long userProfileId) { }
    }

    private sealed class Actor : ICurrentUser
    {
        public bool IsAuthenticated => true; public long UserProfileId => 1; public string? AccountId => "a"; public string? Email => "a@b";
        public string? FullName => "A"; public long RoleId => 1; public int UserTypeId => 1; public int CompanyId => 1;
    }

    private sealed class NoAudit : IAuditWriter
    {
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
