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
        public List<long> ReportingLineRoleIds { get; } = [];

        public Task<IReadOnlyList<LookupItem>> GetReportingLineAsync(long roleId, CancellationToken ct = default)
        {
            ReportingLineRoleIds.Add(roleId);
            return Task.FromResult<IReadOnlyList<LookupItem>>([new LookupItem { TableName = "ReportingLine", Id = 150, Value = "Sales Manager" }]);
        }

        public Task<IReadOnlySet<string>> GetAllowedTableNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SalesPerson_View", "Base_Make" });

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

    [Theory]
    [InlineData("ReportingLine")]
    [InlineData(" reportingline ")]
    public async Task Reporting_line_is_served_for_the_given_role_although_it_is_not_a_whitelisted_table(string source)
    {
        var repository = new FakeLookupRepository();
        IReadOnlyList<LookupItem> rows = await new LookupService(repository).GetScopedAsync(source, roleId: 2);

        Assert.Equal([2L], repository.ReportingLineRoleIds);
        Assert.Empty(repository.ScopedCalls);
        Assert.Equal(150, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task Reporting_line_requires_a_role_id()
    {
        var repository = new FakeLookupRepository();

        await Assert.ThrowsAsync<BusinessRuleException>(() => new LookupService(repository).GetScopedAsync("ReportingLine"));
        Assert.Empty(repository.ReportingLineRoleIds);
    }

    [Fact]
    public async Task Reporting_line_rejects_a_role_id_that_is_not_positive()
    {
        var repository = new FakeLookupRepository();

        await Assert.ThrowsAsync<BusinessRuleException>(() => new LookupService(repository).GetScopedAsync("ReportingLine", 0));
        Assert.Empty(repository.ReportingLineRoleIds);
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
