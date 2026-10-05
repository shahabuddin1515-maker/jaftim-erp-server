using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class UserRegionServiceTests
{
    // Asia(1) -> Eastern Asia(10): Japan 100, China 101, Old Country 102 (inactive)
    //         -> Middle East(11):  UAE 110
    // Europe(2) -> Western Europe(20): France 200
    // Broken shapes: group 99 with no parent, group 98 under a missing division; Test Country 300 has no group.
    private static UserRegionSnapshot Snapshot(long[]? ticked = null, long[]? granted = null) => new(
        [
            new() { EntityId = 1, EntityName = "Asia", EntityTypeId = RegionEntityTypes.Division },
            new() { EntityId = 2, EntityName = "Europe", EntityTypeId = RegionEntityTypes.Division },
            new() { EntityId = 10, EntityName = "Eastern Asia", EntityTypeId = RegionEntityTypes.Group, ParentEntityId = 1 },
            new() { EntityId = 11, EntityName = "Middle East", EntityTypeId = RegionEntityTypes.Group, ParentEntityId = 1 },
            new() { EntityId = 20, EntityName = "Western Europe", EntityTypeId = RegionEntityTypes.Group, ParentEntityId = 2 },
            new() { EntityId = 99, EntityName = "No parent", EntityTypeId = RegionEntityTypes.Group },
            new() { EntityId = 98, EntityName = "Lost parent", EntityTypeId = RegionEntityTypes.Group, ParentEntityId = 50 },
        ],
        [
            new() { CountryId = 100, CountryName = "Japan", IsActive = true, GroupEntityId = 10 },
            new() { CountryId = 101, CountryName = "China", IsActive = true, GroupEntityId = 10 },
            new() { CountryId = 102, CountryName = "Old Country", IsActive = false, GroupEntityId = 10 },
            new() { CountryId = 110, CountryName = "UAE", IsActive = true, GroupEntityId = 11 },
            new() { CountryId = 200, CountryName = "France", IsActive = true, GroupEntityId = 20 },
            new() { CountryId = 300, CountryName = "Test Country", IsActive = true },
            new() { CountryId = 301, CountryName = "In broken group", IsActive = true, GroupEntityId = 99 },
        ],
        ticked ?? [],
        granted ?? []);

    [Fact]
    public async Task A_whole_division_grants_every_active_country_under_it_and_ticks_exactly_their_ancestors()
    {
        var repo = new RegionRepoFake(Snapshot());

        await Service(repo).SaveAsync(7, new SaveUserRegionsRequest(DivisionIds: [1]));

        Assert.Equal(new long[] { 100, 101, 110 }, repo.SavedCountries);   // inactive 102 is not granted
        Assert.Equal(new long[] { 1, 10, 11 }, repo.SavedEntities);
    }

    [Fact]
    public async Task Individual_countries_are_a_partial_group_and_tick_only_their_own_group_and_division()
    {
        var repo = new RegionRepoFake(Snapshot());

        UserRegionsResponse result = await Service(repo).SaveAsync(7, new SaveUserRegionsRequest(CountryIds: [100]));

        Assert.Equal(new long[] { 100 }, repo.SavedCountries);
        Assert.Equal(new long[] { 1, 10 }, repo.SavedEntities);
        RegionDivisionNode asia = result.Divisions.Single(d => d.EntityId == 1);
        Assert.Equal(RegionSelection.Partial, asia.Selection);
        Assert.Equal(RegionSelection.Partial, asia.Groups.Single(g => g.EntityId == 10).Selection);
    }

    [Fact]
    public async Task Whole_groups_and_individual_countries_combine_across_divisions()
    {
        var repo = new RegionRepoFake(Snapshot());

        await Service(repo).SaveAsync(7, new SaveUserRegionsRequest(GroupIds: [20], CountryIds: [110, 110]));

        Assert.Equal(new long[] { 110, 200 }, repo.SavedCountries);
        Assert.Equal(new long[] { 1, 2, 11, 20 }, repo.SavedEntities);
    }

    [Fact]
    public async Task An_inactive_country_cannot_be_newly_assigned_but_an_existing_one_is_kept()
    {
        var fresh = new RegionRepoFake(Snapshot());
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Service(fresh).SaveAsync(7, new SaveUserRegionsRequest(CountryIds: [102])));
        Assert.Contains("Inactive", ex.Message);
        Assert.Null(fresh.SavedCountries);

        var holder = new RegionRepoFake(Snapshot(ticked: [1, 10], granted: [100, 102]));
        await Service(holder).SaveAsync(7, new SaveUserRegionsRequest(CountryIds: [100, 102]));
        Assert.Equal(new long[] { 100, 102 }, holder.SavedCountries);
    }

    [Fact]
    public async Task Every_invalid_id_is_reported_at_once_and_nothing_is_saved()
    {
        var repo = new RegionRepoFake(Snapshot());

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(repo).SaveAsync(7,
            new SaveUserRegionsRequest(DivisionIds: [5, 10], GroupIds: [99, 98, 1], CountryIds: [999, 300, 301])));

        Assert.Contains("Unknown division(s): 5, 10.", ex.Message);              // a group id is not a division
        Assert.Contains("not under a division: 99, 98, 1.", ex.Message);         // broken shapes and a division id
        Assert.Contains("Unknown country(ies): 999.", ex.Message);
        Assert.Contains("not in any group: 300, 301.", ex.Message);              // no group / group without a division
        Assert.Null(repo.SavedCountries);
    }

    [Fact]
    public async Task Customers_cannot_hold_regions_and_an_unknown_user_is_404()
    {
        var users = new FakeUsers();
        users.Profiles[8] = new UserProfileWithRole { UserProfileId = 8, RoleId = 3, IsDeleted = 0 };
        users.Profiles[9] = null;
        var repo = new RegionRepoFake(Snapshot());
        UserRegionService service = Service(repo, users);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.SaveAsync(8, new SaveUserRegionsRequest(CountryIds: [100])));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SaveAsync(9, new SaveUserRegionsRequest(CountryIds: [100])));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(9));
        Assert.Null(repo.SavedCountries);
    }

    [Fact]
    public async Task An_empty_request_clears_the_regions_and_is_audited()
    {
        var repo = new RegionRepoFake(Snapshot(ticked: [1, 10], granted: [100]));
        var audit = new FakeAudit();

        UserRegionsResponse result = await Service(repo, audit: audit).SaveAsync(7, new SaveUserRegionsRequest());

        Assert.Empty(repo.SavedCountries!);
        Assert.Empty(repo.SavedEntities!);
        Assert.Empty(result.GrantedCountryIds);
        Assert.Equal(("UserProfile.RegionsChanged", "UserProfile", (long?)7), Assert.Single(audit.Records));
    }

    [Fact]
    public async Task A_failure_reported_by_the_procedure_is_raised_not_swallowed()
    {
        // Legacy ignored AssignEntitiesToUser's "500" status row and told the screen it had saved.
        var repo = new RegionRepoFake(Snapshot()) { Result = new RegionSaveResult { StatusCode = 500, Message = "Conversion failed" } };
        var audit = new FakeAudit();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(repo, audit: audit).SaveAsync(7, new SaveUserRegionsRequest(CountryIds: [100])));

        Assert.Contains("Conversion failed", ex.Message);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task The_tree_shows_selection_ticks_and_unplaced_countries()
    {
        // 102 is inactive, so Japan + China is ALL of Eastern Asia; UAE is missing, so Asia is partial.
        var repo = new RegionRepoFake(Snapshot(ticked: [1, 10], granted: [100, 101, 300]));

        UserRegionsResponse tree = await Service(repo).GetAsync(7);

        Assert.Equal(new[] { "Asia", "Europe" }, tree.Divisions.Select(d => d.Name));
        RegionDivisionNode asia = tree.Divisions[0];
        Assert.Equal((RegionSelection.Partial, true), (asia.Selection, asia.IsTicked));
        RegionGroupNode eastern = asia.Groups.Single(g => g.EntityId == 10);
        Assert.Equal((RegionSelection.All, true), (eastern.Selection, eastern.IsTicked));
        Assert.Equal(new[] { "China", "Japan", "Old Country" }, eastern.Countries.Select(c => c.Name));
        Assert.False(eastern.Countries.Single(c => c.CountryId == 102).IsActive);
        Assert.Equal(RegionSelection.None, asia.Groups.Single(g => g.EntityId == 11).Selection);
        Assert.Equal(RegionSelection.None, tree.Divisions[1].Selection);
        Assert.Equal(new long[] { 300, 301 }, tree.UnplacedCountries.Select(c => c.CountryId).Order());
        Assert.True(tree.UnplacedCountries.Single(c => c.CountryId == 300).IsGranted);
        Assert.Equal(new long[] { 100, 101, 300 }, tree.GrantedCountryIds);
        Assert.DoesNotContain(tree.Divisions.SelectMany(d => d.Groups), g => g.EntityId is 98 or 99);
    }

    private static UserRegionService Service(RegionRepoFake repo, FakeUsers? users = null, FakeAudit? audit = null) =>
        new(repo, users ?? new FakeUsers(), audit ?? new FakeAudit(), new SaveUserRegionsRequestValidator());

    private sealed class RegionRepoFake(UserRegionSnapshot snapshot) : IUserRegionRepository
    {
        private UserRegionSnapshot _snapshot = snapshot;
        public RegionSaveResult Result { get; init; } = new() { StatusCode = 200, Message = "Entities assigned successfully!" };
        public long[]? SavedEntities { get; private set; }
        public long[]? SavedCountries { get; private set; }

        public Task<UserRegionSnapshot> GetAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult(_snapshot);

        public Task<RegionSaveResult> SaveAsync(long userProfileId, IReadOnlyCollection<long> entityIds, IReadOnlyCollection<long> countryIds, CancellationToken ct = default)
        {
            SavedEntities = entityIds.ToArray();
            SavedCountries = countryIds.ToArray();
            if (Result.StatusCode == 200)
                _snapshot = _snapshot with { TickedEntityIds = SavedEntities, GrantedCountryIds = SavedCountries };
            return Task.FromResult(Result);
        }
    }
}
