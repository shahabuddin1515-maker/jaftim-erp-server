using System.Data;
using Dapper;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>UserRegion_Get (database/v2/008) and the legacy AssignEntitiesToUser.</summary>
public sealed class UserRegionRepository(IDbExecutor db) : IUserRegionRepository
{
    public Task<UserRegionSnapshot> GetAsync(long userProfileId, CancellationToken ct = default) =>
        db.QueryMultipleAsync(
            SpCall.Procedure("UserRegion_Get").With("@UserProfileId", userProfileId).WithoutAudit(),
            async grid => new UserRegionSnapshot(
                (await grid.ReadAsync<RegionEntityRow>()).AsList(),
                (await grid.ReadAsync<RegionCountryRow>()).AsList(),
                (await grid.ReadAsync<long>()).AsList(),
                (await grid.ReadAsync<long>()).AsList()),
            ct);

    /// <summary>
    /// Same parameters as the legacy UserRepository.AssignEntitiesToUser (@UserId, the two CSVs) plus the injected
    /// audit trio the procedure declares. It reports failure as a result row, which the legacy code never read.
    /// </summary>
    public async Task<RegionSaveResult> SaveAsync(long userProfileId, IReadOnlyCollection<long> entityIds, IReadOnlyCollection<long> countryIds, CancellationToken ct = default) =>
        await db.QueryFirstOrDefaultAsync<RegionSaveResult>(
            SpCall.Procedure("AssignEntitiesToUser")
                .With("@UserId", checked((int)userProfileId), DbType.Int32)
                .With("@AssignedEntityCSV", string.Join(",", entityIds), DbType.String)
                .With("@AssignedCountryCSV", string.Join(",", countryIds), DbType.String), ct)
        ?? new RegionSaveResult { StatusCode = 500, Message = "AssignEntitiesToUser returned no status row." };
}
