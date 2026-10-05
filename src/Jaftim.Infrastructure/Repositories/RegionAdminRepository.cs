using System.Data;
using Jaftim.Application.Modules.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>Region_SaveEntity / _DeleteEntity / _MoveCountry (database/v2/009). All declare the audit trio.</summary>
public sealed class RegionAdminRepository(IDbExecutor db) : IRegionAdminRepository
{
    public Task<long> SaveEntityAsync(long? entityId, string name, int entityTypeId, long? parentEntityId, CancellationToken ct = default) =>
        db.QuerySingleAsync<long>(
            SpCall.Procedure("Region_SaveEntity")
                .With("@EntityId", entityId, DbType.Int64)
                .With("@EntityName", name, DbType.String, 200)
                .With("@EntityTypeId", entityTypeId)
                .With("@ParentEntityId", parentEntityId, DbType.Int64), ct);

    public Task DeleteEntityAsync(long entityId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(SpCall.Procedure("Region_DeleteEntity").With("@EntityId", entityId), ct);

    public Task<int> MoveCountryAsync(long countryId, long groupEntityId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(
            SpCall.Procedure("Region_MoveCountry")
                .With("@CountryId", countryId)
                .With("@GroupEntityId", groupEntityId), ct);
}
