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
    /// EXEC Lookup_GetScoped @TableName, @RoleId (database/v2/014) - one whitelisted source, scoped to the caller
    /// (@CreatedBy) by <paramref name="roleId"/> when given, else by the caller's primary role.
    /// </summary>
    Task<IReadOnlyList<LookupItem>> GetScopedValuesAsync(string tableName, long? roleId, CancellationToken ct = default);

    /// <summary>
    /// EXEC Lookup_ReportingLine @RoleId (database/v2/015) - live, active users whose primary role is a direct parent
    /// of <paramref name="roleId"/> in UserHierarchy (legacy SYS_Auth_GetReportingLineByRole).
    /// </summary>
    Task<IReadOnlyList<LookupItem>> GetReportingLineAsync(long roleId, CancellationToken ct = default);
}

public interface ILookupService
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<LookupItem>>> GetDropdownsAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default);
    Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LookupItem>> GetScopedAsync(string tableName, long? roleId = null, CancellationToken ct = default);
}

public sealed class LookupService(ILookupRepository repository) : ILookupService
{
    /// <summary>
    /// A v2 source of the scoped lookup that is not a SYS_DropDownsWithAuth table: the user form's reporting-line
    /// dropdown. Deliberately not added to the whitelist table, because the legacy GetDllAuthTableValues would then
    /// try to SELECT from a table of that name.
    /// </summary>
    public const string ReportingLineSource = "ReportingLine";

    public Task<IReadOnlyList<OfficeLocation>> GetOfficeLocationsAsync(CancellationToken ct = default) =>
        repository.GetOfficeLocationsAsync(ct);

    /// <summary>
    /// The caller's view of one dropdown source. The rules use <paramref name="roleId"/> when the screen sends one
    /// (e.g. stock save loading a list for a role), otherwise the caller's own role; the reporting subtree is always
    /// the caller's. Not a security boundary: GET /api/lookups returns the same rows unscoped. Unknown source -> 404.
    /// <see cref="ReportingLineSource"/> is the exception: there <paramref name="roleId"/> is required and is the role
    /// of the user whose manager is being picked, not the caller's.
    /// </summary>
    public async Task<IReadOnlyList<LookupItem>> GetScopedAsync(string tableName, long? roleId = null, CancellationToken ct = default)
    {
        if (roleId is <= 0)
            throw new BusinessRuleException("roleId must be a positive role id.");

        string name = tableName?.Trim() ?? string.Empty;
        if (string.Equals(name, ReportingLineSource, StringComparison.OrdinalIgnoreCase))
        {
            if (roleId is not { } forRole)
                throw new BusinessRuleException($"roleId is required for {ReportingLineSource}: the role of the user whose reporting line is being chosen.");
            return await repository.GetReportingLineAsync(forRole, ct);
        }

        IReadOnlySet<string> allowed = await repository.GetAllowedTableNamesAsync(ct);
        if (name.Length == 0 || !allowed.Contains(name))
            throw new NotFoundException("Lookup table", name);

        return await repository.GetScopedValuesAsync(name, roleId, ct);
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
