using Hangfire;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Jobs.Jobs;

/// <summary>
/// Port of StockJourneyStatusUpdates/Worker.cs: EXEC UpdateStockJourneyStatus every minute. The procedure takes an
/// app lock (sp_getapplock UpdateStockJourneyStatusLock) and returns immediately when another run holds it, so a
/// slow run overlapping the next tick is safe; DisableConcurrentExecution is belt-and-braces.
/// </summary>
public sealed class StockJourneyStatusJob(TenantScopeRunner tenants, IDbExecutor db, ILogger<StockJourneyStatusJob> logger)
{
    public const string Id = "stock-journey-status";
    public const string Cron = "* * * * *";

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    [AutomaticRetry(Attempts = 0)] // a missed minute is simply picked up by the next tick
    public async Task RunAsync(string tenantCode, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);
        int affected = await db.ExecuteAsync(
            SpCall.Procedure("UpdateStockJourneyStatus").WithoutAudit().WithTimeout(60), ct);
        logger.LogInformation("UpdateStockJourneyStatus ran for {Tenant}; rows affected {Rows}", tenantCode, affected);
    }
}
