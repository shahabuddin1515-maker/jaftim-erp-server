using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Staff users (legacy UserController: UserGetAll, UserDetail, UserSave, ActiveInActiveCall). Customers are a
/// separate module. Role assignments live under /api/users/{id}/roles (UserRolesController).
/// </summary>
public sealed class UsersController(IUserService users) : ApiControllerBase
{
    /// <summary>Staff list (UserGetAll). Visibility follows the caller's primary role exactly as the legacy screen did.</summary>
    [HttpGet]
    [HasPermission(Permissions.EmployeeListing)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<UserListItem>>>> GetAll([FromQuery] int? userTypeId, CancellationToken ct) =>
        Ok(await users.GetAllAsync(userTypeId, ct));

    /// <summary>One staff user (UserGetById).</summary>
    [HttpGet("{userProfileId:long}")]
    [HasPermission(Permissions.EmployeeDetail)]
    public async Task<ActionResult<ApiResponse<UserDetail>>> GetById(long userProfileId, CancellationToken ct) =>
        Ok(await users.GetByIdAsync(userProfileId, ct));

    /// <summary>
    /// Create a staff user. Creates the login (catalog account + this tenant's AspNetUsers row) and the profile
    /// (UserSave). If no password is supplied a temporary one is generated and returned ONCE in this response.
    /// An e-mail that already exists in another company links to that person's existing login instead.
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.EmployeeCreate)]
    public async Task<ActionResult<ApiResponse<UserCreatedResult>>> Create([FromBody] UserUpsertRequest request, CancellationToken ct)
    {
        UserCreatedResult result = await users.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { userProfileId = result.UserProfileId }, ApiResponse.Ok(result, "User created."));
    }

    /// <summary>Update a staff user (UserSave). Email and password are not changed here.</summary>
    [HttpPut("{userProfileId:long}")]
    [HasPermission(Permissions.EmployeeCreate)]
    public async Task<ActionResult<ApiResponse<object?>>> Update(long userProfileId, [FromBody] UserUpsertRequest request, CancellationToken ct)
    {
        await users.UpdateAsync(userProfileId, request, ct);
        return Ok("User updated.");
    }

    /// <summary>Flip Active/Inactive (ActiveInActiveCall). Deactivating revokes the user's live API sessions.</summary>
    [HttpPost("{userProfileId:long}/toggle-active")]
    [HasPermission(Permissions.EmployeeCreate)]
    public async Task<ActionResult<ApiResponse<object>>> ToggleActive(long userProfileId, CancellationToken ct) =>
        Ok<object>(new { statusId = await users.ToggleActiveAsync(userProfileId, ct) }, "Status updated.");
}
