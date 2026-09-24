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
}
