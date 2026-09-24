using Jaftim.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Jaftim.Infrastructure.Data;

/// <summary>Opens connections to the CURRENT TENANT database (resolved from ITenantContext).</summary>
public interface IDbConnectionFactory
{
    /// <summary>A new, opened connection. Callers own it - always <c>await using</c>.</summary>
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>Opens connections to the shared CATALOG database (tenants, accounts, sessions, auth audit).</summary>
public interface ICatalogConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    /// <summary>Catalog connection string; ConnectionStrings:Catalog is used if this is empty.</summary>
    public string? CatalogConnectionString { get; set; }
    /// <summary>Default command timeout for SP calls. Dapper's default (30s) cut off long imports in the legacy app.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;
}

public sealed class CatalogConnectionFactory(IOptions<DatabaseOptions> options) : ICatalogConnectionFactory
{
    private readonly string _connectionString = options.Value.CatalogConnectionString
        ?? throw new InvalidOperationException("Database:CatalogConnectionString (or ConnectionStrings:Catalog) is not configured.");

    public Task<SqlConnection> OpenAsync(CancellationToken ct = default) => ConnectionOpener.OpenAsync(_connectionString, ct);
}

/// <summary>
/// Tenant-bound factory: asks the resolver for the current tenant's connection string on every open (the resolver
/// caches). Throws when no tenant is bound - a tenant-bound repository must never run without one.
/// </summary>
public sealed class TenantConnectionFactory(ITenantContext tenant, ITenantConnectionResolver resolver) : IDbConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (!tenant.HasTenant)
            throw new InvalidOperationException("No tenant is bound to the current scope; tenant-bound data access is not possible.");
        string connectionString = await resolver.GetConnectionStringAsync(tenant.TenantId, ct);
        return await ConnectionOpener.OpenAsync(connectionString, ct);
    }
}

internal static class ConnectionOpener
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
