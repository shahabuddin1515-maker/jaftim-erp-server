using System.Data;
using Dapper;

namespace Jaftim.Infrastructure.Data;

/// <summary>
/// Describes one stored-procedure call. Built fluently by repositories:
/// <code>SpCall.Procedure("StockGetById").With("@StockId", id)</code>
/// Audit parameters (@CreatedBy, @CreatedAt, @CompanyId) are added by <see cref="IDbExecutor"/> for every call unless
/// <see cref="WithoutAudit"/> is used - the legacy DBRepository injected them inconsistently across overloads;
/// here the default is ON and opting out is explicit and visible at the call site.
/// </summary>
public sealed class SpCall
{
    private SpCall(string name, CommandType commandType)
    {
        Name = name;
        CommandType = commandType;
    }

    public string Name { get; }
    public CommandType CommandType { get; }
    public DynamicParameters Parameters { get; } = new();
    public bool InjectAudit { get; private set; } = true;
    public int? CommandTimeoutSeconds { get; private set; }

    public static SpCall Procedure(string name) => new(name, CommandType.StoredProcedure);

    /// <summary>Only for the handful of legacy inline statements (e.g. UPDATE StockImages ...). Prefer a procedure.</summary>
    public static SpCall Text(string sql) => new(sql, CommandType.Text);

    public SpCall With(string name, object? value, DbType? dbType = null, int? size = null)
    {
        Parameters.Add(name, value, dbType, ParameterDirection.Input, size);
        return this;
    }

    /// <summary>Table-valued parameter (e.g. TYPE_BankStatementDetail, BulkInquiryType).</summary>
    public SpCall WithTable(string name, DataTable table, string typeName)
    {
        Parameters.Add(name, table.AsTableValuedParameter(typeName));
        return this;
    }

    public SpCall WithOutput(string name, DbType dbType, int? size = null)
    {
        Parameters.Add(name, dbType: dbType, direction: ParameterDirection.Output, size: size);
        return this;
    }

    /// <summary>Skip @CreatedBy/@CreatedAt/@CompanyId. Use only for procedures that do not declare them (e.g. BaseMakeGetAll, UpdateStockJourneyStatus).</summary>
    public SpCall WithoutAudit()
    {
        InjectAudit = false;
        return this;
    }

    public SpCall WithTimeout(int seconds)
    {
        CommandTimeoutSeconds = seconds;
        return this;
    }
}
