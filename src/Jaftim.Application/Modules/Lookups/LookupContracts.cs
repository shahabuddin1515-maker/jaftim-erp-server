using Jaftim.Domain.Entities.Lookups;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Lookups;

/// <summary>
/// Dropdown data. Wraps the legacy generic dropdown subsystem (README section 3.2B): SYS_DropDownsWithAuth is the
/// whitelist of tables GetDllAuthTableValues may read, so the API exposes the same whitelist and nothing else.
/// </summary>
public interface ILookupRepository
{
    /// <summary>EXEC GetDllAuthTableValues @csv_TableNames - id/value rows for every whitelisted table requested.</summary>
    Task<IReadOnlyList<LookupItem>> GetDropdownValuesAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default);

    /// <summary>SYS_DropDownsWithAuth table names - the only values GetDropdownValuesAsync accepts.</summary>
    Task<IReadOnlySet<string>> GetAllowedTableNamesAsync(CancellationToken ct = default);

    /// <summary>EXEC Lookup_OfficeLocations (database/v2/013) - Base_Country rows with IsLocationCountry = 1.</summary>
    Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default);

    /// <summary>
    /// EXEC Lookup_GetScoped @TableName (database/v2/014) - one whitelisted source, scoped to the caller (@CreatedBy)
    /// by their primary role: SalesPerson_View to their reporting subtree, Customer_View to an agent's tagged customers.
    /// </summary>
    Task<IReadOnlyList<LookupItem>> GetScopedValuesAsync(string tableName, CancellationToken ct = default);
}

public interface ILookupService
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<LookupItem>>> GetDropdownsAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default);
    Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LookupItem>> GetScopedAsync(string tableName, CancellationToken ct = default);
}

public sealed class LookupService(ILookupRepository repository) : ILookupService
{
    public Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default) =>
        repository.GetOfficeLocationsAsync(ct);

    /// <summary>
    /// The caller's view of one dropdown source. The scope comes from the caller's identity inside the procedure,
    /// never from the request, so it cannot be widened by leaving a parameter out. Unknown source -> 404.
    /// </summary>
    public async Task<IReadOnlyList<LookupItem>> GetScopedAsync(string tableName, CancellationToken ct = default)
    {
        string name = tableName?.Trim() ?? string.Empty;
        IReadOnlySet<string> allowed = await repository.GetAllowedTableNamesAsync(ct);
        if (name.Length == 0 || !allowed.Contains(name))
            throw new NotFoundException("Lookup table", name);

        return await repository.GetScopedValuesAsync(name, ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LookupItem>>> GetDropdownsAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default)
    {
        if (tableNames.Count == 0)
            throw new BusinessRuleException("At least one table name is required.");

        IReadOnlySet<string> allowed = await repository.GetAllowedTableNamesAsync(ct);
        string[] unknown = tableNames.Where(t => !allowed.Contains(t)).ToArray();
        if (unknown.Length > 0)
            throw new BusinessRuleException($"Unknown lookup table(s): {string.Join(", ", unknown)}.");

        IReadOnlyList<LookupItem> rows = await repository.GetDropdownValuesAsync(tableNames, ct);
        return rows
            .GroupBy(r => r.TableName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LookupItem>)g.ToList(), StringComparer.OrdinalIgnoreCase);
    }
}
