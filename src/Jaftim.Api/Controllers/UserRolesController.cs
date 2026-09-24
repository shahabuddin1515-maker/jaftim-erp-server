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
/// Multiple roles per user, optionally time-bound. The PRIMARY role (UserProfile.RoleId) is what the legacy
/// procedures see; extra roles widen the API permission set. Permission 202 = the legacy user-edit screen
/// (Employee Create), where the role was assigned before.
/// </summary>
[Route("api/users/{userProfileId:long}/roles")]
public sealed class UserRolesController(IUserRoleService roles, IValidator<AssignRoleRequest> assignValidator) : ApiControllerBase
{
    /// <summary>All role assignments of a user, including expired/future ones. IsPrimary marks UserProfile.RoleId.</summary>
    [HttpGet]
    [HasPermission(Permissions.EmployeeDetail)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UserRoleAssignment>>>> List(long userProfileId, CancellationToken ct) =>
        Ok(await roles.ListAsync(userProfileId, ct));

    /// <summary>Assign a role, or re-scope an existing assignment. Null dates mean immediately / permanent.</summary>
    [HttpPost]
    [HasPermission(Permissions.EmployeeCreate)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UserRoleAssignment>>>> Assign(long userProfileId, [FromBody] AssignRoleRequest request, CancellationToken ct)
    {
        await assignValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await roles.AssignAsync(userProfileId, request, ct), "Role assigned.");
    }

    /// <summary>Revoke a non-primary role immediately.</summary>
    [HttpDelete("{roleId:long}")]
    [HasPermission(Permissions.EmployeeCreate)]
    public async Task<ActionResult<ApiResponse<object?>>> Revoke(long userProfileId, long roleId, CancellationToken ct)
    {
        await roles.RevokeAsync(userProfileId, roleId, ct);
        return Ok("Role revoked.");
    }

    /// <summary>Change the primary role (what the stored procedures use for scoping and approval routing).</summary>
    [HttpPut("primary")]
    [HasPermission(Permissions.EmployeeCreate)]
    public async Task<ActionResult<ApiResponse<object?>>> SetPrimary(long userProfileId, [FromBody] SetPrimaryRoleRequest request, CancellationToken ct)
    {
        await roles.SetPrimaryAsync(userProfileId, request, ct);
        return Ok("Primary role updated.");
    }
}
