using System.Data;
using Dapper;
using Jaftim.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Jaftim.Infrastructure.Data;

/// <summary>
/// The single Dapper gateway every repository uses. Owns connection lifetime, audit-parameter injection and the
/// command timeout. Repositories never open connections or new-up SqlCommand themselves.
/// </summary>
public interface IDbExecutor
{
    Task<IReadOnlyList<T>> QueryAsync<T>(SpCall call, CancellationToken ct = default);
    Task<T?> QueryFirstOrDefaultAsync<T>(SpCall call, CancellationToken ct = default);
    Task<T> QuerySingleAsync<T>(SpCall call, CancellationToken ct = default);
    Task<int> ExecuteAsync(SpCall call, CancellationToken ct = default);
    /// <summary>For procedures that return several result sets (count + page, payload + recipients, ...).</summary>
    Task<T> QueryMultipleAsync<T>(SpCall call, Func<SqlMapper.GridReader, Task<T>> read, CancellationToken ct = default);
    /// <summary>Runs <paramref name="work"/> inside one transaction; use when a flow needs two SPs to succeed or fail together.</summary>
    Task<T> InTransactionAsync<T>(Func<IDbTransactionScope, Task<T>> work, IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken ct = default);
}

/// <summary>
/// Same gateway, bound to the shared catalog database. Catalog procedures never declare the legacy audit trio, so
/// calls through this executor should use <see cref="SpCall.WithoutAudit"/> (the executor enforces it).
/// </summary>
public interface ICatalogDbExecutor : IDbExecutor;

/// <summary>A transaction-bound executor handed to <see cref="IDbExecutor.InTransactionAsync{T}"/> callbacks.</summary>
public interface IDbTransactionScope
{
    Task<IReadOnlyList<T>> QueryAsync<T>(SpCall call, CancellationToken ct = default);
    Task<T?> QueryFirstOrDefaultAsync<T>(SpCall call, CancellationToken ct = default);
    Task<int> ExecuteAsync(SpCall call, CancellationToken ct = default);
}

/// <summary>Tenant-bound executor: opens the current tenant database and injects the legacy audit trio by default.</summary>
public sealed class DbExecutor(
    IDbConnectionFactory connections,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IOptions<DatabaseOptions> options)
    : DbExecutorBase(connections.OpenAsync, currentUser, clock, options.Value.CommandTimeoutSeconds, injectAuditByDefault: true);

/// <summary>Catalog-bound executor: opens the catalog database; audit injection is always off (catalog procedures do not declare it).</summary>
public sealed class CatalogDbExecutor(
    ICatalogConnectionFactory connections,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    IOptions<DatabaseOptions> options)
    : DbExecutorBase(connections.OpenAsync, currentUser, clock, options.Value.CommandTimeoutSeconds, injectAuditByDefault: false), ICatalogDbExecutor;

public abstract class DbExecutorBase(
    Func<CancellationToken, Task<SqlConnection>> open,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    int defaultTimeout,
    bool injectAuditByDefault) : IDbExecutor
{
    private readonly int _defaultTimeout = defaultTimeout;
    private readonly Func<CancellationToken, Task<SqlConnection>> _open = open;

    private Task<SqlConnection> OpenAsync(CancellationToken ct) => _open(ct);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(SpCall call, CancellationToken ct = default)
    {
        await using SqlConnection connection = await OpenAsync(ct);
        IEnumerable<T> rows = await connection.QueryAsync<T>(Build(call, null, ct));
        return rows.AsList();
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(SpCall call, CancellationToken ct = default)
    {
        await using SqlConnection connection = await OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<T>(Build(call, null, ct));
    }

    public async Task<T> QuerySingleAsync<T>(SpCall call, CancellationToken ct = default)
    {
        await using SqlConnection connection = await OpenAsync(ct);
        return await connection.QuerySingleAsync<T>(Build(call, null, ct));
    }

    public async Task<int> ExecuteAsync(SpCall call, CancellationToken ct = default)
    {
        await using SqlConnection connection = await OpenAsync(ct);
        return await connection.ExecuteAsync(Build(call, null, ct));
    }

    public async Task<T> QueryMultipleAsync<T>(SpCall call, Func<SqlMapper.GridReader, Task<T>> read, CancellationToken ct = default)
    {
        await using SqlConnection connection = await OpenAsync(ct);
        using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(Build(call, null, ct));
        return await read(grid);
    }

    public async Task<T> InTransactionAsync<T>(Func<IDbTransactionScope, Task<T>> work, IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken ct = default)
    {
        await using SqlConnection connection = await OpenAsync(ct);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolation, ct);
        try
        {
            T result = await work(new TransactionScope(this, connection, transaction));
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Adds the audit trio the stored procedures declare as trailing parameters:
    /// @CreatedBy = UserProfileId, @CreatedAt = UtcNow, @CompanyId. Identical to the legacy DBRepository contract,
    /// applied uniformly. Do not change UtcNow to local time - see README section 20 (Date/Time caveat).
    /// </summary>
    private CommandDefinition Build(SpCall call, IDbTransaction? transaction, CancellationToken ct)
    {
        if (call.InjectAudit && injectAuditByDefault)
        {
            call.Parameters.Add("@CreatedBy", currentUser.UserProfileId, DbType.Int64);
            call.Parameters.Add("@CreatedAt", clock.UtcNow, DbType.DateTime);
            call.Parameters.Add("@CompanyId", currentUser.CompanyId, DbType.Int64);
        }

        return new CommandDefinition(
            call.Name,
            call.Parameters,
            transaction,
            call.CommandTimeoutSeconds ?? _defaultTimeout,
            call.CommandType,
            cancellationToken: ct);
    }

    private sealed class TransactionScope(DbExecutorBase owner, SqlConnection connection, SqlTransaction transaction) : IDbTransactionScope
    {
        public async Task<IReadOnlyList<T>> QueryAsync<T>(SpCall call, CancellationToken ct = default) =>
            (await connection.QueryAsync<T>(owner.Build(call, transaction, ct))).AsList();

        public Task<T?> QueryFirstOrDefaultAsync<T>(SpCall call, CancellationToken ct = default) =>
            connection.QueryFirstOrDefaultAsync<T>(owner.Build(call, transaction, ct));

        public Task<int> ExecuteAsync(SpCall call, CancellationToken ct = default) =>
            connection.ExecuteAsync(owner.Build(call, transaction, ct));
    }
}
