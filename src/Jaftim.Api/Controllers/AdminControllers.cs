using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Audit;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Audit;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>Tenant registry (read-only for now; tenants are provisioned by script). Gated by Assign Roles (304: Super Admin + Sales Manager today). Not 300 - the legacy mapping hands the Role module root to Sales Executives as well.</summary>
public sealed class TenantsController(ITenantService tenants) : ApiControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TenantSummary>>>> GetAll(CancellationToken ct) =>
        Ok(await tenants.GetAllAsync(ct));
}

/// <summary>Business audit trail of the current tenant (AuditLog). GET /api/audit?entityType=UserProfile&amp;entityId=1&amp;page=1.</summary>
public sealed class AuditController(IAuditQueryService audit) : ApiControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.AssignRoles)]
    public async Task<ActionResult<ApiResponse<PagedResult<AuditEntry>>>> Get([FromQuery] string entityType, [FromQuery] long? entityId, [FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await audit.GetByEntityAsync(entityType, entityId, paging, ct));
}
