using System.Data;
using Hangfire;
using Jaftim.Application.Abstractions;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Jobs.Jobs;

/// <summary>
/// Port of the StockJourneyStatusUpdates outbox trio (StockStatusTimeScheduler + StockStatusOutboxPublisher +
/// StockStatusQueueWorker) WITHOUT the Azure Storage Queue hop: the outbox row itself is the queue.
///
///   every minute : StockStatusRefreshOutbox_EnqueueTimeDependent   (time-based triggers, e.g. port date passed)
///   every minute : StockStatusRefreshOutbox_Claim  -> one Hangfire job per row on the "stock-status" queue
///   per row      : StockStatus_RefreshOne -> RefreshStockDependentChecks (8 nested Update*_Auto procs)
///                  then MarkPublished / MarkFailed with the lease id.
///
/// The DB-side contract (Claim / MarkPublished / MarkFailed with @LeaseId, plus the trg_StockStatusRefresh_* triggers
/// that insert outbox rows) is unchanged, so the legacy worker can keep running side by side until cut-over.
/// README section 11.2 documents the enqueue-predicate invariant: never widen the enqueue query beyond what the
/// refresh can actually change, or stocks loop forever at 8 procedure calls a minute.
/// </summary>
public sealed class StockStatusOutboxEnqueueJob(TenantScopeRunner tenants, IDbExecutor db, ILogger<StockStatusOutboxEnqueueJob> logger)
{
    public const string Id = "stock-status-outbox-enqueue";
    public const string Cron = "* * * * *";

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(string tenantCode, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);
        await db.ExecuteAsync(
            SpCall.Procedure("StockStatusRefreshOutbox_EnqueueTimeDependent").WithoutAudit().WithTimeout(60), ct);
        logger.LogDebug("StockStatusRefreshOutbox_EnqueueTimeDependent ran");
    }
}

public sealed class StockStatusOutboxDispatchJob(TenantScopeRunner tenants, IDbExecutor db, IJobScheduler scheduler, ILogger<StockStatusOutboxDispatchJob> logger)
{
    public const string Id = "stock-status-outbox-dispatch";
    public const string Cron = "* * * * *";
    private const int BatchSize = 32;
    private const int LeaseSeconds = 120;
    private const int MaxBatchesPerRun = 20;

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(string tenantCode, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);
        int dispatched = 0;
        for (int batch = 0; batch < MaxBatchesPerRun && !ct.IsCancellationRequested; batch++)
        {
            IReadOnlyList<OutboxItem> items = await db.QueryAsync<OutboxItem>(
                SpCall.Procedure("StockStatusRefreshOutbox_Claim")
                    .With("@BatchSize", BatchSize)
                    .With("@LeaseSeconds", LeaseSeconds)
                    .WithoutAudit(), ct);

            if (items.Count == 0) break;

            foreach (OutboxItem item in items)
                scheduler.Enqueue<StockStatusRefreshOneJob>(j => j.RunAsync(tenantCode, item.OutboxId, item.StockId, item.LeaseId, CancellationToken.None), JobQueues.StockStatus);

            dispatched += items.Count;
            if (items.Count < BatchSize) break;
        }

        if (dispatched > 0) logger.LogInformation("Dispatched {Count} stock-status refresh jobs", dispatched);
    }

    /// <summary>Column order/names of StockStatusRefreshOutbox_Claim: OutboxId, StockId, EventType, CreatedAtUtc, LeaseId.</summary>
    public sealed record OutboxItem(long OutboxId, long StockId, string EventType, DateTime CreatedAtUtc, Guid LeaseId);
}

public sealed class StockStatusRefreshOneJob(TenantScopeRunner tenants, IDbExecutor db, ILogger<StockStatusRefreshOneJob> logger)
{
    // The lease is 120s; retrying beyond it would race a re-claim, so retries stay inside that window.
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [10, 30])]
    public async Task RunAsync(string tenantCode, long outboxId, long stockId, Guid leaseId, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);
        try
        {
            await db.ExecuteAsync(
                SpCall.Procedure("StockStatus_RefreshOne").With("@StockId", stockId).WithoutAudit().WithTimeout(120), ct);
            await db.ExecuteAsync(
                SpCall.Procedure("StockStatusRefreshOutbox_MarkPublished")
                    .With("@OutboxId", outboxId).With("@LeaseId", leaseId).WithoutAudit(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stock-status refresh failed for stock {StockId} (outbox {OutboxId})", stockId, outboxId);
            await db.ExecuteAsync(
                SpCall.Procedure("StockStatusRefreshOutbox_MarkFailed")
                    .With("@OutboxId", outboxId)
                    .With("@LeaseId", leaseId)
                    .With("@LastError", ex.Message[..Math.Min(ex.Message.Length, 2000)], DbType.String)
                    .WithoutAudit(), CancellationToken.None);
            throw;
        }
    }
}
