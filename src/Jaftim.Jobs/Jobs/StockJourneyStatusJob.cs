using Hangfire;
using Jaftim.Application.Jobs;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Jobs.Jobs;

/// <summary>
/// Port of StockJourneyStatusUpdates/Worker.cs: EXEC UpdateStockJourneyStatus every minute. The procedure takes an
/// app lock (sp_getapplock UpdateStockJourneyStatusLock) and returns immediately when another run holds it, so a
/// slow run overlapping the next tick is safe; DisableConcurrentExecution is belt-and-braces.
/// </summary>
public sealed class StockJourneyStatusJob(IJobRunner jobs, IDbExecutor db, ILogger<StockJourneyStatusJob> logger)
{
    public const string Id = "stock-journey-status";
    public const string Cron = "* * * * *";

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    [AutomaticRetry(Attempts = 0)] // a missed minute is simply picked up by the next tick
    public Task RunAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(Id, tenantCode), async c =>
        {
            int affected = await db.ExecuteAsync(
                SpCall.Procedure("UpdateStockJourneyStatus").WithoutAudit().WithTimeout(60), c);
            logger.LogInformation("UpdateStockJourneyStatus ran for {Tenant}; rows affected {Rows}", tenantCode, affected);
        }, ct);
}
