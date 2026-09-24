using FluentValidation;
using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Navigation;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Navigation;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// The navbar as data. Modules -> items -> sub-items, each gated by a RoleAction permission and/or role list;
/// the frontend renders whatever /me returns and maps each node's Code to a route component.
/// </summary>
public sealed class NavigationController(INavigationService navigation, IValidator<NavigationItemUpsert> validator) : ApiControllerBase
{
    /// <summary>The navbar for the caller: only modules/items their effective roles may open, plus the first route to land on.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<NavigationResponse>>> Me(CancellationToken ct) =>
        Ok(await navigation.GetForCurrentUserAsync(ct));

    /// <summary>Every navigation item, flat, including inactive ones (admin screen).</summary>
    [HttpGet]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<NavigationItem>>>> GetAll(CancellationToken ct) =>
        Ok(await navigation.GetAllAsync(ct));

    /// <summary>Create or update one item (by NavigationItemId). Parent is given by code; codes must be unique.</summary>
    [HttpPut]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<NavigationItem>>> Upsert([FromBody] NavigationItemUpsert request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await navigation.UpsertAsync(request, ct), "Navigation item saved.");
    }

    /// <summary>Soft-delete an item and everything under it.</summary>
    [HttpDelete("{navigationItemId:int}")]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<object?>>> Delete(int navigationItemId, CancellationToken ct)
    {
        await navigation.DeleteAsync(navigationItemId, ct);
        return Ok("Navigation item deleted.");
    }
}

/// <summary>The permission catalog (RoleAction hierarchy). The same module grouping drives the navbar, the role-rights editor and per-button hiding.</summary>
public sealed class PermissionsController(IPermissionCatalogService catalog, IValidator<PermissionUpsert> validator) : ApiControllerBase
{
    /// <summary>Create a permission (a new module when parentActionId is null, otherwise a screen/action under the parent) or rename / re-parent one. Ids 900+ are allocated by the database.</summary>
    [HttpPut]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<PermissionNode>>> Upsert([FromBody] PermissionUpsert request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await catalog.UpsertAsync(request, ct), "Permission saved.");
    }

    /// <summary>Every RoleAction as a tree: module -> screen -> button. Values are the ActionIds used by [HasPermission] and returned in /api/auth/me.</summary>
    [HttpGet("catalog")]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PermissionNode>>>> Catalog(CancellationToken ct) =>
        Ok(await catalog.GetCatalogAsync(ct));
}
