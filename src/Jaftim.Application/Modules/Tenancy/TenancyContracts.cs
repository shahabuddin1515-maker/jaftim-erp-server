using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Tenancy;

namespace Jaftim.Application.Modules.Tenancy;

/// <summary>Catalog Tenant table (Catalog_Tenant_* procedures).</summary>
public interface ITenantRepository
{
    /// <summary>Catalog_Tenant_GetAll - every tenant WITH its connection string. Tenant resolution and jobs only.</summary>
    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct = default);
    Task<Tenant?> GetByCodeAsync(string code, CancellationToken ct = default);
    /// <summary>Catalog_Tenant_GetPage - the admin list, searched/sorted/paged in SQL; never selects the connection string.</summary>
    Task<PagedResult<TenantSummary>> GetPageAsync(PagedRequest paging, CancellationToken ct = default);
}

public sealed record TenantSummary(int TenantId, string Code, string Name, bool IsActive);

public interface ITenantService
{
    /// <summary>Tenants (admin), paged. Connection strings are never returned.</summary>
    Task<PagedResult<TenantSummary>> GetAllAsync(PagedRequest paging, CancellationToken ct = default);
}

public sealed class TenantService(ITenantRepository tenants) : ITenantService
{
    public Task<PagedResult<TenantSummary>> GetAllAsync(PagedRequest paging, CancellationToken ct = default) =>
        tenants.GetPageAsync(paging, ct);
}
