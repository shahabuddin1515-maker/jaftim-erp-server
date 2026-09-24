using System.Data;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>Legacy RolesGetAll / RoleGetById / RoleSave / GetRoleRightsByRoleId plus v2 RoleAction_Save / RoleActionMapping_Replace.</summary>
public sealed class RoleRepository(IDbExecutor db) : IRoleRepository
{
    public Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default) =>
        db.QueryAsync<Role>(SpCall.Procedure("RolesGetAll"), ct);

    public Task<Role?> GetByIdAsync(long roleId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<Role>(SpCall.Procedure("RoleGetById").With("@RoleId", roleId), ct);

    public Task SaveAsync(RoleUpsert role, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("RoleSave")
                .With("@RoleId", role.RoleId ?? 0)
                .With("@RoleName", role.RoleName, DbType.String)
                .With("@DefaultPath", role.DefaultPath, DbType.String)
                .With("@UserTypeId", role.UserTypeId), ct);

    public async Task<IReadOnlySet<int>> GetGrantedActionIdsAsync(long roleId, CancellationToken ct = default)
    {
        // GetRoleRightsByRoleId returns every RoleAction with IsChecked = 1 when the role holds it.
        IReadOnlyList<RoleRight> rows = await db.QueryAsync<RoleRight>(SpCall.Procedure("GetRoleRightsByRoleId").With("@RoleId", roleId), ct);
        return rows.Where(r => r.IsChecked == 1).Select(r => r.ActionId).ToHashSet();
    }

    public Task<int> ReplaceGrantsAsync(long roleId, IReadOnlyCollection<int> actionIds, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(
            SpCall.Procedure("RoleActionMapping_Replace")
                .With("@RoleId", roleId)
                .With("@ActionIdsCsv", string.Join(",", actionIds), DbType.String)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);

    public Task<int> SavePermissionAsync(PermissionUpsert permission, long actorUserProfileId, CancellationToken ct = default) =>
        db.QuerySingleAsync<int>(
            SpCall.Procedure("RoleAction_Save")
                .With("@ActionId", permission.ActionId)
                .With("@ActionName", permission.Name, DbType.String, 200)
                .With("@ActionParentId", permission.ParentActionId ?? 0)
                .With("@ActorUserProfileId", actorUserProfileId)
                .WithoutAudit(), ct);

    private sealed class RoleRight
    {
        public int ActionId { get; set; }
        public int IsChecked { get; set; }
    }
}
