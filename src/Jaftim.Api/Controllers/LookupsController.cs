using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Modules.Lookups;
using Jaftim.Domain.Entities.Lookups;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>Dropdown data. Legacy: AuthFilterController.GetDllAuthTableValues (load.dll.js). Table names must be in SYS_DropDownsWithAuth.</summary>
public sealed class LookupsController(ILookupService lookups) : ApiControllerBase
{
    /// <summary>GET /api/lookups?tables=Base_Make,Base_Country - one array per requested table.</summary>
    [HttpGet]
    [HasPermission(Permissions.AuthTable)]
    public async Task<ActionResult<ApiResponse<IReadOnlyDictionary<string, IReadOnlyList<LookupItem>>>>> Get([FromQuery] string tables, CancellationToken ct)
    {
        string[] names = (tables ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Ok(await lookups.GetDropdownsAsync(names, ct));
    }

    /// <summary>
    /// The company's office-location countries: Base_Country rows with IsLocationCountry = 1 (live and active), sorted
    /// by name - <c>{ id, name, alpha2Code }</c>. A short fixed list, so not paged. The ids are Base_Country ids: do not
    /// send them as <c>officeLocationCountryId</c> on the user endpoints, which stores Base_OriginCountry ids.
    /// </summary>
    [HttpGet("office-locations")]
    [HasPermission(Permissions.AuthTable)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<OfficeLocation>>>> GetOfficeLocations(CancellationToken ct) =>
        Ok(await lookups.GetOfficeLocationsAsync(ct));

    /// <summary>
    /// GET /api/lookups/scoped/SalesPerson_View - one dropdown source (any name the plain lookup accepts) filtered to
    /// the signed-in user, as <c>[{ tableName, id, value }]</c> sorted by value. The rules below use <c>roleId</c> when
    /// it is sent (optional, e.g. stock save loading a list for a given role), otherwise the caller's own role from the
    /// token; "below them" always means below the signed-in user:
    /// <list type="bullet">
    /// <item><c>SalesPerson_View</c>: Super Admin every row; System Admin every row outside user types 1/7; anyone
    /// else only the people who report to them, directly or not, and not themself (a Team Lead Sales sees their team;
    /// someone with nobody under them gets an empty list).</item>
    /// <item><c>Customer_View</c>: a Sales Executive only the customers tagged to them; a CSD Manager or Team Lead
    /// Sales the customers tagged to them or anyone under them; anyone else every row.</item>
    /// <item><b>Role-filtered sources</b> (tables/views registered in LookupRoleFilter): only the rows whose role
    /// column equals <c>roleId</c>, which is then <b>required</b> (missing -> 422) and is never replaced by the caller's
    /// role. Today: <c>vw_UserHierarchy</c>, the user form's "Reporting Line" dropdown - <c>roleId</c> is the role of
    /// the user being created or edited, and the rows are the active users whose primary role is a direct parent of
    /// it in UserHierarchy (e.g. Sales Executive -> Sales Managers and Team Leads Sales).</item>
    /// <item>Every other source: every row, the same as <c>GET /api/lookups</c>.</item>
    /// </list>
    /// Unknown source -> 404; <c>roleId</c> of 0 or less -> 422. Not paged (a dropdown source).
    /// </summary>
    [HttpGet("scoped/{table}")]
    [HasPermission(Permissions.AuthTable)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<LookupItem>>>> GetScoped(string table, [FromQuery] long? roleId, CancellationToken ct) =>
        Ok(await lookups.GetScopedAsync(table, roleId, ct));
}
