using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class PermissionKeyServiceTests
{
    [Fact]
    public void Keys_are_upper_snake_of_the_name_and_a_later_duplicate_gets_its_id()
    {
        IReadOnlyDictionary<string, int> keys = PermissionKeyService.BuildKeys(
        [
            Action(550, "Customer Tagging"),
            Action(417, "Stock Listing"),
            Action(526, "Customer Tagging"),
            Action(591, "Document - Draft BL"),
            Action(576, "Not Sold (Admin Only)"),
            Action(900, "  "),
            Action(901, "3rd Party"),
        ]);

        Assert.Equal(417, keys["STOCK_LISTING"]);
        Assert.Equal(526, keys["CUSTOMER_TAGGING"]);          // lowest id keeps the plain key regardless of input order
        Assert.Equal(550, keys["CUSTOMER_TAGGING_550"]);
        Assert.Equal(591, keys["DOCUMENT_DRAFT_BL"]);
        Assert.Equal(576, keys["NOT_SOLD_ADMIN_ONLY"]);
        Assert.Equal(900, keys["ACTION_900"]);
        Assert.Equal(901, keys["_3RD_PARTY"]);
        Assert.Equal([417, 526, 550, 576, 591, 900, 901], keys.Values);   // response order is ActionId order
    }

    [Fact]
    public async Task Without_a_code_the_lowest_id_active_tenant_is_bound_before_reading()
    {
        var setter = new Setter();
        var service = new PermissionKeyService(new Tenants(), setter, new FakeUsers { AllRoleActions = [Action(417, "Stock Listing")] });

        IReadOnlyDictionary<string, int> keys = await service.GetKeysAsync(null);

        Assert.Equal((2, "jaftim"), (setter.TenantId, setter.TenantCode));
        Assert.Equal(417, keys["STOCK_LISTING"]);
    }

    [Fact]
    public async Task A_given_code_selects_that_tenant()
    {
        var setter = new Setter();
        await new PermissionKeyService(new Tenants(), setter, new FakeUsers()).GetKeysAsync(" acme ");
        Assert.Equal((3, "acme"), (setter.TenantId, setter.TenantCode));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("old")]   // inactive
    public async Task Unknown_or_inactive_tenant_is_404_and_binds_nothing(string code)
    {
        var setter = new Setter();
        await Assert.ThrowsAsync<NotFoundException>(() => new PermissionKeyService(new Tenants(), setter, new FakeUsers()).GetKeysAsync(code));
        Assert.Null(setter.TenantId);
    }

    private static RoleAction Action(int id, string name) => new() { ActionId = id, ActionName = name };

    private sealed class Tenants : ITenantRepository
    {
        private static readonly Tenant[] All =
        [
            new() { TenantId = 1, Code = "old", IsActive = false },
            new() { TenantId = 3, Code = "acme", IsActive = true },
            new() { TenantId = 2, Code = "jaftim", IsActive = true },
        ];

        public Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Tenant>>(All);
        public Task<Tenant?> GetByCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(All.FirstOrDefault(t => t.Code.Equals(code, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class Setter : ITenantContextSetter
    {
        public int? TenantId { get; private set; }
        public string? TenantCode { get; private set; }
        public void Set(int tenantId, string tenantCode) => (TenantId, TenantCode) = (tenantId, tenantCode);
    }
}
