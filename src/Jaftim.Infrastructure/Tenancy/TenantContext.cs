using System.Data;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Infrastructure.Data;
using Microsoft.Extensions.Caching.Memory;

namespace Jaftim.Infrastructure.Tenancy;

/// <summary>
/// Scoped, settable tenant context. The API sets it from the JWT in the authentication pipeline; Jobs set it per
/// tenant run; the login flow sets it once the membership has been chosen.
/// </summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    public bool HasTenant { get; private set; }
    public int TenantId { get; private set; }
    public string TenantCode { get; private set; } = string.Empty;

    public void Set(int tenantId, string tenantCode)
    {
        TenantId = tenantId;
        TenantCode = tenantCode;
        HasTenant = tenantId > 0;
    }
}

/// <summary>
/// Tenant id -> connection string from the catalog, cached 5 minutes. A value of the form "kv:&lt;secret&gt;" is
/// resolved through <see cref="ISecretResolver"/> (App Service/Key Vault in UAT/Live; identity in Development).
/// </summary>
public sealed class CachedTenantConnectionResolver(ITenantRepository tenants, ISecretResolver secrets, IMemoryCache cache) : ITenantConnectionResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<string> GetConnectionStringAsync(int tenantId, CancellationToken ct = default)
    {
        string? value = await cache.GetOrCreateAsync($"tenant:cs:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            IReadOnlyList<Tenant> all = await tenants.GetAllAsync(ct);
            Tenant tenant = all.FirstOrDefault(t => t.TenantId == tenantId && t.IsActive)
                ?? throw new InvalidOperationException($"Tenant {tenantId} is unknown or inactive.");
            return await secrets.ResolveAsync(tenant.ConnectionString, ct);
        });
        return value!;
    }
}

/// <summary>Turns "kv:name" references into secret values. The default resolves nothing (plain connection strings).</summary>
public interface ISecretResolver
{
    Task<string> ResolveAsync(string valueOrReference, CancellationToken ct = default);
}

public sealed class PassThroughSecretResolver : ISecretResolver
{
    public Task<string> ResolveAsync(string valueOrReference, CancellationToken ct = default) =>
        valueOrReference.StartsWith("kv:", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("Key Vault references require a Key Vault-backed ISecretResolver registration.")
            : Task.FromResult(valueOrReference);
}

/// <summary>Catalog_Tenant_* procedures.</summary>
public sealed class TenantRepository(ICatalogDbExecutor catalog) : ITenantRepository
{
    public Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct = default) =>
        catalog.QueryAsync<Tenant>(SpCall.Procedure("Catalog_Tenant_GetAll").WithoutAudit(), ct);

    public Task<Tenant?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        catalog.QueryFirstOrDefaultAsync<Tenant>(
            SpCall.Procedure("Catalog_Tenant_GetByCode").With("@Code", code, DbType.String, 50).WithoutAudit(), ct);
}
