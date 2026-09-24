namespace Jaftim.Application.Abstractions;

/// <summary>
/// The tenant (company) the current unit of work runs against. Each tenant has its own database; every tenant-bound
/// repository call goes to that database. The API sets it from the JWT "tid" claim; Jobs set it explicitly per run.
/// </summary>
public interface ITenantContext
{
    bool HasTenant { get; }
    int TenantId { get; }
    string TenantCode { get; }
}

/// <summary>Lets a host (Jobs, tests, the login flow before a token exists) bind the scoped tenant context explicitly.</summary>
public interface ITenantContextSetter
{
    void Set(int tenantId, string tenantCode);
}

/// <summary>Resolves a tenant id to its database connection string (catalog Tenant table, cached).</summary>
public interface ITenantConnectionResolver
{
    Task<string> GetConnectionStringAsync(int tenantId, CancellationToken ct = default);
}
