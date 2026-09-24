using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Tenancy;

namespace Jaftim.Jobs;

/// <summary>
/// Runs a unit of work with the scoped tenant context bound to one tenant. Every tenant-bound repository resolved
/// inside the scope hits that tenant's database. Hangfire creates a scope per job execution, so jobs simply call
/// <see cref="BindAsync"/> first; <see cref="ForEachTenantAsync"/> is for jobs that must visit all tenants.
/// </summary>
public sealed class TenantScopeRunner(ITenantRepository tenants, ITenantContextSetter setter, IServiceScopeFactory scopes, ILogger<TenantScopeRunner> logger)
{
    /// <summary>Binds the current scope to the tenant with this code. Throws if unknown/inactive.</summary>
    public async Task<Tenant> BindAsync(string tenantCode, CancellationToken ct)
    {
        Tenant tenant = await tenants.GetByCodeAsync(tenantCode, ct)
            ?? throw new InvalidOperationException($"Tenant '{tenantCode}' is unknown.");
        if (!tenant.IsActive) throw new InvalidOperationException($"Tenant '{tenantCode}' is inactive.");
        setter.Set(tenant.TenantId, tenant.Code);
        return tenant;
    }

    /// <summary>Runs <paramref name="work"/> once per active tenant, each in its own scope; one failure does not stop the others.</summary>
    public async Task ForEachTenantAsync(Func<IServiceProvider, Tenant, CancellationToken, Task> work, CancellationToken ct)
    {
        foreach (Tenant tenant in (await tenants.GetAllAsync(ct)).Where(t => t.IsActive))
        {
            using IServiceScope scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant.TenantId, tenant.Code);
            try
            {
                await work(scope.ServiceProvider, tenant, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Tenant {TenantCode} failed", tenant.Code);
            }
        }
    }
}
