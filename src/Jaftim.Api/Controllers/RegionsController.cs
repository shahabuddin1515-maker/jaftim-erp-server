using FluentValidation;
using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// The region tree (Division -> Group -> Country) as master data - permission 905 "Master Data" (Settings). New in v2:
/// the legacy app maintained it by SQL script. Every write returns the updated tree. Assigning regions to a user is
/// <c>/api/users/{id}/regions</c>. No operation here changes what any user can access; moves re-derive the affected
/// users' tick rows so the legacy assignment screen stays consistent.
/// </summary>
[Route("api/regions")]
[HasPermission(Permissions.MasterData)]
public sealed class RegionsController(
    IRegionAdminService regions,
    IValidator<SaveDivisionRequest> divisionValidator,
    IValidator<SaveGroupRequest> groupValidator,
    IValidator<MoveCountryRequest> moveValidator) : ApiControllerBase
{
    /// <summary>The whole tree, plus orphan groups and unplaced countries to repair.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> Get(CancellationToken ct) =>
        Ok(await regions.GetTreeAsync(ct));

    /// <summary>Create a division. Names are unique among divisions (409 otherwise).</summary>
    [HttpPost("divisions")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> CreateDivision([FromBody] SaveDivisionRequest request, CancellationToken ct)
    {
        await divisionValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await regions.CreateDivisionAsync(request, ct), "Division created.");
    }

    /// <summary>Rename a division.</summary>
    [HttpPut("divisions/{divisionId:long}")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> UpdateDivision(long divisionId, [FromBody] SaveDivisionRequest request, CancellationToken ct)
    {
        await divisionValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await regions.UpdateDivisionAsync(divisionId, request, ct), "Division updated.");
    }

    /// <summary>Delete an empty division (422 while it still has groups).</summary>
    [HttpDelete("divisions/{divisionId:long}")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> DeleteDivision(long divisionId, CancellationToken ct) =>
        Ok(await regions.DeleteDivisionAsync(divisionId, ct), "Division deleted.");

    /// <summary>Create a group under a division. Names are unique among groups (409 otherwise).</summary>
    [HttpPost("groups")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> CreateGroup([FromBody] SaveGroupRequest request, CancellationToken ct)
    {
        await groupValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await regions.CreateGroupAsync(request, ct), "Group created.");
    }

    /// <summary>Rename a group and/or move it (with its countries) to another division; also repairs an orphan group.</summary>
    [HttpPut("groups/{groupId:long}")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> UpdateGroup(long groupId, [FromBody] SaveGroupRequest request, CancellationToken ct)
    {
        await groupValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await regions.UpdateGroupAsync(groupId, request, ct), "Group updated.");
    }

    /// <summary>Delete an empty group (422 while it still has countries).</summary>
    [HttpDelete("groups/{groupId:long}")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> DeleteGroup(long groupId, CancellationToken ct) =>
        Ok(await regions.DeleteGroupAsync(groupId, ct), "Group deleted.");

    /// <summary>Move a country to another group (or place an unplaced one). Users' granted countries are unchanged.</summary>
    [HttpPut("countries/{countryId:long}/group")]
    public async Task<ActionResult<ApiResponse<RegionTreeResponse>>> MoveCountry(long countryId, [FromBody] MoveCountryRequest request, CancellationToken ct)
    {
        await moveValidator.ValidateAndThrowAppAsync(request, ct);
        return Ok(await regions.MoveCountryAsync(countryId, request, ct), "Country moved.");
    }
}
