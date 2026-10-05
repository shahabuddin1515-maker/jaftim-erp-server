using FluentValidation;
using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// A user's regions (Division -> Group -> Country). Replaces UserController.AssignEntities (screen gated by
/// _CSS_548 "User Hierarchy") and the read-only entity tab of UserDetail / UserEntityHierarchy (Employee Detail, 203).
/// The granted countries are what scopes the user's data (customer lists, tagging divisions).
/// </summary>
[Route("api/users/{userProfileId:long}/regions")]
public sealed class UserRegionsController(IUserRegionService regions, IValidator<SaveUserRegionsRequest> validator) : ApiControllerBase
{
    /// <summary>The full region tree with this user's selection per division, group and country.</summary>
    [HttpGet]
    [HasPermission(Permissions.EmployeeDetail)]
    public async Task<ActionResult<ApiResponse<UserRegionsResponse>>> Get(long userProfileId, CancellationToken ct) =>
        Ok(await regions.GetAsync(userProfileId, ct));

    /// <summary>
    /// Replace the user's regions. Whole divisions/groups expand to their active countries now (countries added to a
    /// group later are not picked up until the next save); countryIds add individual countries. Empty clears all.
    /// Unknown ids, a country in no group, or a newly picked inactive country are a 422.
    /// </summary>
    [HttpPut]
    [HasPermission(Permissions.UserHierarchy)]
    public async Task<ActionResult<ApiResponse<UserRegionsResponse>>> Save(long userProfileId, [FromBody] SaveUserRegionsRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await regions.SaveAsync(userProfileId, request, ct), "Regions saved.");
    }
}
