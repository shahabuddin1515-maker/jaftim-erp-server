using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class RegionAdminServiceTests
{
    // Asia(1) -> Eastern Asia(10): Japan 100;  Middle East(11): empty
    // Europe(2) -> no groups
    // Orphan group 99 (no parent) holding country 301; Test Country 300 has no group; country 302 points at a missing group.
    private static UserRegionSnapshot Tree() => new(
        [
            new() { EntityId = 1, EntityName = "Asia", EntityTypeId = RegionEntityTypes.Division },
            new() { EntityId = 2, EntityName = "Europe", EntityTypeId = RegionEntityTypes.Division },
            new() { EntityId = 10, EntityName = "Eastern Asia", EntityTypeId = RegionEntityTypes.Group, ParentEntityId = 1 },
            new() { EntityId = 11, EntityName = "Middle East", EntityTypeId = RegionEntityTypes.Group, ParentEntityId = 1 },
            new() { EntityId = 99, EntityName = "Lost group", EntityTypeId = RegionEntityTypes.Group },
        ],
        [
            new() { CountryId = 100, CountryName = "Japan", IsActive = true, GroupEntityId = 10 },
            new() { CountryId = 300, CountryName = "Test Country", IsActive = true },
            new() { CountryId = 301, CountryName = "Stranded", IsActive = true, GroupEntityId = 99 },
            new() { CountryId = 302, CountryName = "Pointing nowhere", IsActive = true, GroupEntityId = 4242 },
        ],
        [], []);

    [Fact]
    public async Task The_tree_separates_valid_regions_orphan_groups_and_unplaced_countries()
    {
        RegionTreeResponse tree = await Service(new AdminRepoFake()).GetTreeAsync();

        Assert.Equal(new[] { "Asia", "Europe" }, tree.Divisions.Select(d => d.Name));
        Assert.Equal(new[] { "Eastern Asia", "Middle East" }, tree.Divisions[0].Groups.Select(g => g.Name));
        RegionAdminGroup orphan = Assert.Single(tree.OrphanGroups);
        Assert.Equal((99L, (long?)null, 301L), (orphan.EntityId, orphan.DivisionId, Assert.Single(orphan.Countries).CountryId));
        Assert.Equal(new long[] { 302, 300 }, tree.UnplacedCountries.Select(c => c.CountryId));  // ordered by name
    }

    [Fact]
    public async Task Names_are_trimmed_and_unique_per_type_case_insensitively()
    {
        var repo = new AdminRepoFake();
        RegionAdminService service = Service(repo);

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateDivisionAsync(new SaveDivisionRequest("  asia ")));
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateGroupAsync(new SaveGroupRequest("LOST GROUP", 2)));  // orphans count
        await service.CreateGroupAsync(new SaveGroupRequest(" Asia ", 2));      // a group may share a division's name
        await service.UpdateDivisionAsync(1, new SaveDivisionRequest("ASIA"));   // renaming to its own name in new case is fine

        Assert.Equal(new[] { "save:-:Asia:2:2", "save:1:ASIA:1:-" }, repo.Calls);
    }

    [Fact]
    public async Task A_group_needs_a_live_division_and_an_unknown_region_is_404()
    {
        RegionAdminService service = Service(new AdminRepoFake());

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateGroupAsync(new SaveGroupRequest("New", 10)));  // 10 is a group
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.CreateGroupAsync(new SaveGroupRequest("New", 77)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateDivisionAsync(10, new SaveDivisionRequest("X")));  // a group id
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateGroupAsync(1, new SaveGroupRequest("X", 2)));      // a division id
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteGroupAsync(555));
    }

    [Fact]
    public async Task An_orphan_group_is_repaired_by_giving_it_a_division()
    {
        var repo = new AdminRepoFake();

        await Service(repo).UpdateGroupAsync(99, new SaveGroupRequest("Lost group", 2));

        Assert.Equal("save:99:Lost group:2:2", Assert.Single(repo.Calls));
    }

    [Fact]
    public async Task Only_empty_regions_can_be_deleted()
    {
        var repo = new AdminRepoFake();
        RegionAdminService service = Service(repo);

        var division = await Assert.ThrowsAsync<BusinessRuleException>(() => service.DeleteDivisionAsync(1));
        Assert.Contains("2 group(s)", division.Message);
        var group = await Assert.ThrowsAsync<BusinessRuleException>(() => service.DeleteGroupAsync(10));
        Assert.Contains("1 country(ies)", group.Message);
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.DeleteGroupAsync(99));   // orphan, but still holds a country

        await service.DeleteGroupAsync(11);
        await service.DeleteDivisionAsync(2);
        Assert.Equal(new[] { "delete:11", "delete:2" }, repo.Calls);
    }

    [Fact]
    public async Task A_country_moves_only_to_a_placed_group_and_a_no_op_move_writes_nothing()
    {
        var repo = new AdminRepoFake();
        var audit = new FakeAudit();
        RegionAdminService service = Service(repo, audit);

        await Assert.ThrowsAsync<NotFoundException>(() => service.MoveCountryAsync(999, new MoveCountryRequest(11)));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.MoveCountryAsync(300, new MoveCountryRequest(99)));  // orphan group
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.MoveCountryAsync(300, new MoveCountryRequest(1)));   // a division

        await service.MoveCountryAsync(100, new MoveCountryRequest(10));   // already there
        Assert.Empty(repo.Calls);

        await service.MoveCountryAsync(300, new MoveCountryRequest(11));   // place an unplaced country
        Assert.Equal("move:300:11", Assert.Single(repo.Calls));
        Assert.Equal(("Region.CountryMoved", "Country", (long?)300), Assert.Single(audit.Records));
    }

    [Fact]
    public async Task Every_write_is_audited()
    {
        var audit = new FakeAudit();
        RegionAdminService service = Service(new AdminRepoFake(), audit);

        await service.CreateDivisionAsync(new SaveDivisionRequest("Antarctica"));
        await service.UpdateDivisionAsync(2, new SaveDivisionRequest("Europe & UK"));
        await service.CreateGroupAsync(new SaveGroupRequest("Gulf", 1));
        await service.UpdateGroupAsync(11, new SaveGroupRequest("Middle East", 2));
        await service.DeleteGroupAsync(11);

        Assert.Equal(new[] { "Region.DivisionCreated", "Region.DivisionUpdated", "Region.GroupCreated", "Region.GroupUpdated", "Region.GroupDeleted" },
            audit.Records.Select(r => r.Action));
    }

    private static RegionAdminService Service(AdminRepoFake repo, FakeAudit? audit = null) =>
        new(repo, new SnapshotRepo(), audit ?? new FakeAudit(),
            new SaveDivisionRequestValidator(), new SaveGroupRequestValidator(), new MoveCountryRequestValidator());

    private sealed class SnapshotRepo : IUserRegionRepository
    {
        public Task<UserRegionSnapshot> GetAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult(Tree());
        public Task<RegionSaveResult> SaveAsync(long userProfileId, IReadOnlyCollection<long> entityIds, IReadOnlyCollection<long> countryIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class AdminRepoFake : IRegionAdminRepository
    {
        public List<string> Calls { get; } = [];

        public Task<long> SaveEntityAsync(long? entityId, string name, int entityTypeId, long? parentEntityId, CancellationToken ct = default)
        {
            Calls.Add($"save:{entityId?.ToString() ?? "-"}:{name}:{entityTypeId}:{parentEntityId?.ToString() ?? "-"}");
            return Task.FromResult(entityId ?? 500);
        }

        public Task DeleteEntityAsync(long entityId, CancellationToken ct = default)
        {
            Calls.Add($"delete:{entityId}");
            return Task.CompletedTask;
        }

        public Task<int> MoveCountryAsync(long countryId, long groupEntityId, CancellationToken ct = default)
        {
            Calls.Add($"move:{countryId}:{groupEntityId}");
            return Task.FromResult(0);
        }
    }
}
