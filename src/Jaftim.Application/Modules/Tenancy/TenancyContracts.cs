using Jaftim.Domain.Entities.Tenancy;

namespace Jaftim.Application.Modules.Tenancy;

/// <summary>Catalog Tenant table (Catalog_Tenant_* procedures).</summary>
public interface ITenantRepository
{
    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct = default);
    Task<Tenant?> GetByCodeAsync(string code, CancellationToken ct = default);
}

public sealed record TenantSummary(int TenantId, string Code, string Name, bool IsActive);

public interface ITenantService
{
    /// <summary>All tenants (admin). Connection strings are never returned.</summary>
    Task<IReadOnlyList<TenantSummary>> GetAllAsync(CancellationToken ct = default);
}

public sealed class TenantService(ITenantRepository tenants) : ITenantService
{
    public async Task<IReadOnlyList<TenantSummary>> GetAllAsync(CancellationToken ct = default) =>
        (await tenants.GetAllAsync(ct)).Select(t => new TenantSummary(t.TenantId, t.Code, t.Name, t.IsActive)).ToList();
}
