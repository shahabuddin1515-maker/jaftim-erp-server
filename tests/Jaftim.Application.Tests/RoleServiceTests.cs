using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Navigation;
using Jaftim.Domain.Entities.Users;

namespace Jaftim.Application.Tests;

public sealed class RoleServiceTests
{
    // Stock (400) -> Stock Detail (413) -> Stock Export (558-ish, here 401 "Stock Edit" under 413)
    private static readonly Dictionary<int, RoleAction> Catalog = new RoleAction[]
    {
        new() { ActionId = 400, ActionName = "Stock", ActionParentId = 0 },
        new() { ActionId = 413, ActionName = "Stock Detail", ActionParentId = 400 },
        new() { ActionId = 401, ActionName = "Stock Edit", ActionParentId = 413 },
        new() { ActionId = 417, ActionName = "Stock Listing", ActionParentId = 400 },
        new() { ActionId = 100, ActionName = "Customer", ActionParentId = 0 },
    }.ToDictionary(a => a.ActionId);

    [Fact]
    public void Granting_an_action_grants_its_screen_and_module()
    {
        IReadOnlySet<int> closed = RoleService.WithAncestors([401], Catalog);
        Assert.Equal(new HashSet<int> { 401, 413, 400 }, closed);
    }

    [Fact]
    public void Closure_is_idempotent_and_ignores_unknown_ids()
    {
        IReadOnlySet<int> closed = RoleService.WithAncestors([417, 400, 9999], Catalog);
        Assert.Equal(new HashSet<int> { 417, 400 }, closed);
    }

    [Fact]
    public void Navigation_node_without_a_permission_is_never_shown()
    {
        var item = new NavigationItem { Code = "x", Title = "X", Route = "/x", ActionId = null, IsActive = true };
        Assert.False(Application.Modules.Navigation.NavigationService.GatePasses(item, new HashSet<int> { 1, 2, 3 }, new HashSet<long> { 1 }));
    }
}
