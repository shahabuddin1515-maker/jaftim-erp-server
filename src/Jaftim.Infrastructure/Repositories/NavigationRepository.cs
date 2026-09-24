using System.Data;
using Jaftim.Application.Modules.Navigation;
using Jaftim.Domain.Entities.Navigation;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>Navigation_* procedures (database/v2/003_Navigation.sql). v2 procedures: explicit actor, no legacy audit trio.</summary>
public sealed class NavigationRepository(IDbExecutor db) : INavigationRepository
{
    public Task<IReadOnlyList<NavigationItem>> GetAllAsync(CancellationToken ct = default) =>
        db.QueryAsync<NavigationItem>(SpCall.Procedure("Navigation_GetAll").WithoutAudit(), ct);

    public Task<int> SaveAsync(NavigationItem item, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(
            SpCall.Procedure("Navigation_Save")
                .With("@NavigationItemId", item.NavigationItemId == 0 ? null : item.NavigationItemId)
                .With("@ParentId", item.ParentId)
                .With("@Code", item.Code, DbType.String, 100)
                .With("@Title", item.Title, DbType.String, 100)
                .With("@Icon", item.Icon, DbType.String, 100)
                .With("@Route", item.Route, DbType.String, 200)
                .With("@SortOrder", item.SortOrder)
                .With("@ActionId", item.ActionId)
                .With("@RequiredRoleIdsCsv", item.RequiredRoleIdsCsv, DbType.String, 200)
                .With("@IsActive", item.IsActive)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);

    public Task<int> DeleteAsync(int navigationItemId, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(
            SpCall.Procedure("Navigation_Delete")
                .With("@NavigationItemId", navigationItemId)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);
}
