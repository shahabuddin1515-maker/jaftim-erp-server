using System.Data;
using Jaftim.Application.Modules.Lookups;
using Jaftim.Domain.Entities.Lookups;
using Jaftim.Infrastructure.Data;
using Microsoft.Extensions.Caching.Memory;

namespace Jaftim.Infrastructure.Repositories;

public sealed class LookupRepository(IDbExecutor db, IMemoryCache cache) : ILookupRepository
{
    private const string AllowedTablesCacheKey = "lookups:allowed-tables";

    public Task<IReadOnlyList<LookupItem>> GetDropdownValuesAsync(IReadOnlyCollection<string> tableNames, CancellationToken ct = default) =>
        db.QueryAsync<LookupItem>(
            SpCall.Procedure("GetDllAuthTableValues")
                .With("@CSV_TableNames", string.Join(",", tableNames), DbType.String), ct);

    public async Task<IReadOnlySet<string>> GetAllowedTableNamesAsync(CancellationToken ct = default)
    {
        IReadOnlySet<string>? cached = await cache.GetOrCreateAsync(AllowedTablesCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            IReadOnlyList<string> names = await db.QueryAsync<string>(
                SpCall.Text("SELECT TableName FROM dbo.SYS_DropDownsWithAuth").WithoutAudit(), ct);
            return (IReadOnlySet<string>)names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        });
        return cached ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
