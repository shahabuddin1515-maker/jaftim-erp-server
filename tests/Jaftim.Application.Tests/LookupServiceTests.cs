using Jaftim.Application.Modules.Lookups;
using Jaftim.Domain.Entities.Lookups;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class LookupServiceTests
{
    private sealed class FakeLookupRepository : ILookupRepository
    {
        public List<string> ScopedCalls { get; } = [];

        public Task<IReadOnlySet<string>> GetAllowedTableNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SalesPerson_View", "Base_Make" });

        public Task<IReadOnlyList<LookupItem>> GetScopedValuesAsync(string tableName, CancellationToken ct = default)
        {
            ScopedCalls.Add(tableName);
            return Task.FromResult<IReadOnlyList<LookupItem>>([new LookupItem { TableName = tableName, Id = 7, Value = "Team Lead" }]);
        }

        public Task<IReadOnlyList<LookupItem>> GetDropdownValuesAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Scoped_lookup_reads_a_whitelisted_source_trimmed()
    {
        var repository = new FakeLookupRepository();
        IReadOnlyList<LookupItem> rows = await new LookupService(repository).GetScopedAsync("  salesperson_view ");

        Assert.Equal(["salesperson_view"], repository.ScopedCalls);
        Assert.Equal(7, Assert.Single(rows).Id);
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
