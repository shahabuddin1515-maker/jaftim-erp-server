using FluentValidation;
using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Roles and the permissions each role holds (RoleActionMapping). Legacy: UserController.RolesGetAll / RoleSave /
/// AssignRoles. Changing a role takes effect for signed-in users within 5 minutes (role cache) - no re-login.
/// </summary>
public sealed class RolesController(IRoleService roles, IValidator<RoleUpsert> roleValidator) : ApiControllerBase
{
    /// <summary>All active roles.</summary>
    [HttpGet]
    [HasPermission(Permissions.RoleListing)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<Role>>>> GetAll(CancellationToken ct) =>
        Ok(await roles.GetAllAsync(ct));

    /// <summary>Create or rename a role.</summary>
    [HttpPut]
    [HasPermission(Permissions.RoleSaveEdit)]
    public async Task<ActionResult<ApiResponse<Role>>> Upsert([FromBody] RoleUpsert request, CancellationToken ct)
    {
        await roleValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await roles.UpsertAsync(request, ct), "Role saved.");
    }

    /// <summary>The permission tree (module -> screen -> action) with granted = true where this role holds the node.</summary>
    [HttpGet("{roleId:long}/permissions")]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RolePermissionNode>>>> GetPermissions(long roleId, CancellationToken ct) =>
        Ok(await roles.GetPermissionsAsync(roleId, ct));

    /// <summary>Replace the role's permission set. Ancestors are added automatically; removed rows are soft-deleted; the role cache is invalidated.</summary>
    [HttpPut("{roleId:long}/permissions")]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RolePermissionNode>>>> SetPermissions(long roleId, [FromBody] SetRolePermissionsRequest request, CancellationToken ct) =>
        Ok(await roles.SetPermissionsAsync(roleId, request, ct), "Permissions saved.");
}
