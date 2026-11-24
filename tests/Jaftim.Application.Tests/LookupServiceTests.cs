using Jaftim.Application.Modules.Lookups;
using Jaftim.Domain.Entities.Lookups;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class LookupServiceTests
{
    private sealed class FakeLookupRepository : ILookupRepository
    {
        public List<string> ScopedCalls { get; } = [];
        public List<long?> ScopedRoleIds { get; } = [];
        public Task<IReadOnlySet<string>> GetAllowedTableNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SalesPerson_View", "Base_Make", "vw_UserHierarchy" });

        public Task<IReadOnlyList<LookupItem>> GetScopedValuesAsync(string tableName, long? roleId, CancellationToken ct = default)
        {
            ScopedCalls.Add(tableName);
            ScopedRoleIds.Add(roleId);
            return Task.FromResult<IReadOnlyList<LookupItem>>([new LookupItem { TableName = tableName, Id = 7, Value = "Team Lead" }]);
        }

        public Task<IReadOnlyList<LookupItem>> GetDropdownValuesAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Country>> GetCountriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Country>>([new Country { Id = 133, Name = "Pakistan", DialCodeId = 140, DialCode = "+92" }]);

        public List<long> CityCalls { get; } = [];
        public Task<IReadOnlyList<City>> GetCitiesAsync(long countryId, CancellationToken ct = default)
        {
            CityCalls.Add(countryId);
            return Task.FromResult<IReadOnlyList<City>>(
                [new City { Id = 3, Name = "Mandeville" }, new City { Id = 1, Name = "kingston" }, new City { Id = 2, Name = "Barrett Town" }]);
        }
    }

    [Fact]
    public async Task Cities_of_a_country_come_back_sorted_by_name()
    {
        var repository = new FakeLookupRepository();
        IReadOnlyList<City> cities = await new LookupService(repository).GetCitiesAsync(84);

        Assert.Equal([84L], repository.CityCalls);
        Assert.Equal(["Barrett Town", "kingston", "Mandeville"], cities.Select(c => c.Name));   // case-insensitive
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public async Task Cities_need_a_positive_country_id(long countryId)
    {
        var repository = new FakeLookupRepository();
        await Assert.ThrowsAsync<BusinessRuleException>(() => new LookupService(repository).GetCitiesAsync(countryId));
        Assert.Empty(repository.CityCalls);
    }

    [Fact]
    public async Task Scoped_lookup_reads_a_whitelisted_source_trimmed()
    {
        var repository = new FakeLookupRepository();
        IReadOnlyList<LookupItem> rows = await new LookupService(repository).GetScopedAsync("  salesperson_view ");

        Assert.Equal(["salesperson_view"], repository.ScopedCalls);
        Assert.Equal([null], repository.ScopedRoleIds);   // no roleId -> the procedure uses the caller's own role
        Assert.Equal(7, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task Scoped_lookup_passes_a_requested_role_through()
    {
        var repository = new FakeLookupRepository();
        await new LookupService(repository).GetScopedAsync("SalesPerson_View", roleId: 11);

        Assert.Equal([11L], repository.ScopedRoleIds);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Scoped_lookup_rejects_a_role_id_that_is_not_positive(long roleId)
    {
        var repository = new FakeLookupRepository();

        await Assert.ThrowsAsync<BusinessRuleException>(() => new LookupService(repository).GetScopedAsync("SalesPerson_View", roleId));
        Assert.Empty(repository.ScopedCalls);
    }

    [Fact]
    public async Task A_whitelisted_view_goes_through_the_same_generic_path_with_its_role()
    {
        // vw_UserHierarchy is role-filtered in SQL (LookupRoleFilter, database/v2/015); the service has no per-source code.
        var repository = new FakeLookupRepository();
        await new LookupService(repository).GetScopedAsync("vw_UserHierarchy", roleId: 2);

        Assert.Equal(["vw_UserHierarchy"], repository.ScopedCalls);
        Assert.Equal([2L], repository.ScopedRoleIds);
    }

    [Theory]
    [InlineData("UserProfile")]
    [InlineData("SalesPerson_View; DROP TABLE Role")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Scoped_lookup_rejects_anything_outside_the_whitelist_without_touching_the_database(string table)
    {
        var repository = new FakeLookupRepository();

        await Assert.ThrowsAsync<NotFoundException>(() => new LookupService(repository).GetScopedAsync(table));
        Assert.Empty(repository.ScopedCalls);
    }
}
