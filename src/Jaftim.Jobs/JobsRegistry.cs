using Hangfire;
using Hangfire.Storage;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Jobs.Jobs;
using Microsoft.Extensions.Options;

namespace Jaftim.Jobs;

/// <summary>
/// Recurring jobs are registered PER TENANT with id "{job}:{tenantCode}". <see cref="TenantJobsRegistrarJob"/> runs
/// at startup and every 5 minutes, adding schedules for new tenants and removing them for inactive/deleted ones,
/// so provisioning a tenant needs nothing more than its catalog row. Adding a job = add the class under Jobs/ and
/// one line in <see cref="RegisterForTenant"/>. Registration is idempotent (AddOrUpdate by id).
/// </summary>
public static class JobsRegistry
{
    public static readonly string[] Queues =
    [
        JobQueues.Critical,
        JobQueues.Default,
        JobQueues.StockStatus,
        JobQueues.LegacySync,
        JobQueues.Integrations,
    ];

    private static readonly string[] PerTenantJobIds =
    [
        StockJourneyStatusJob.Id, StockStatusOutboxEnqueueJob.Id, StockStatusOutboxDispatchJob.Id,
        AccountSyncJob.Id, PartyKindSyncJob.Id, RespondIoContactSyncJob.Id, RespondIoCustomerPushJob.Id, RespondIoConversationSyncJob.Id,
    ];

    public static string IdFor(string jobId, string tenantCode) => $"{jobId}:{tenantCode}";

    public static void RegisterGlobal(IRecurringJobManager manager)
    {
        manager.AddOrUpdate<TenantJobsRegistrarJob>(TenantJobsRegistrarJob.Id, j => j.RunAsync(CancellationToken.None), "*/5 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    public static void RegisterForTenant(IRecurringJobManager manager, Tenant tenant, RespondIoOptions respondIo)
    {
        var options = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        string code = tenant.Code;

        // --- Stock journey/status (StockJourneyStatusUpdates project) ---
        manager.AddOrUpdate<StockJourneyStatusJob>(IdFor(StockJourneyStatusJob.Id, code), j => j.RunAsync(code, CancellationToken.None), StockJourneyStatusJob.Cron, options);
        manager.AddOrUpdate<StockStatusOutboxEnqueueJob>(IdFor(StockStatusOutboxEnqueueJob.Id, code), j => j.RunAsync(code, CancellationToken.None), StockStatusOutboxEnqueueJob.Cron, options);
        manager.AddOrUpdate<StockStatusOutboxDispatchJob>(IdFor(StockStatusOutboxDispatchJob.Id, code), j => j.RunAsync(code, CancellationToken.None), StockStatusOutboxDispatchJob.Cron, options);

        // --- Identity: tenant credentials -> catalog ---
        manager.AddOrUpdate<AccountSyncJob>(IdFor(AccountSyncJob.Id, code), j => j.RunAsync(code, CancellationToken.None), AccountSyncJob.Cron, options);

        // --- Normalized columns vs. the paths this backend does not own (lead pipeline, Respond.io, legacy app) ---
        manager.AddOrUpdate<PartyKindSyncJob>(IdFor(PartyKindSyncJob.Id, code), j => j.RunAsync(code, CancellationToken.None), PartyKindSyncJob.Cron, options);

        // --- Respond.io (RespondIOSync project). Intervals mirror the legacy appsettings. ---
        manager.AddOrUpdate<RespondIoContactSyncJob>(IdFor(RespondIoContactSyncJob.Id, code), j => j.RunAsync(code, CancellationToken.None), HoursToCron(respondIo.IntervalInHours), options);
        manager.AddOrUpdate<RespondIoCustomerPushJob>(IdFor(RespondIoCustomerPushJob.Id, code), j => j.RunAsync(code, CancellationToken.None), HoursToCron(respondIo.IntervalInHours), options);
        manager.AddOrUpdate<RespondIoConversationSyncJob>(IdFor(RespondIoConversationSyncJob.Id, code), j => j.RunAsync(code, CancellationToken.None), HoursToCron(respondIo.ConversationSyncIntervalHours), options);

        // --- LegacyErpStockSyncJob is fire-and-forget (enqueued by the API), not recurring. ---
    }

    /// <summary>Hangfire (Cronos) only accepts hour intervals of 1-23; anything longer becomes a daily run.</summary>
    private static string HoursToCron(int hours) => hours >= 24 ? Cron.Daily() : Cron.HourInterval(Math.Max(1, hours));

    public static void RemoveForTenant(IRecurringJobManager manager, string tenantCode)
    {
        foreach (string jobId in PerTenantJobIds)
            manager.RemoveIfExists(IdFor(jobId, tenantCode));
    }
}

/// <summary>Reconciles the recurring schedules with the catalog Tenant table.</summary>
public sealed class TenantJobsRegistrarJob(
    IRecurringJobManager manager,
    JobStorage storage,
    ITenantRepository tenants,
    IOptions<RespondIoOptions> respondIo,
    ILogger<TenantJobsRegistrarJob> logger)
{
    public const string Id = "tenant-jobs-registrar";

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        IReadOnlyList<Tenant> all = await tenants.GetAllAsync(ct);
        var active = all.Where(t => t.IsActive).ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);

        foreach (Tenant tenant in active.Values)
            JobsRegistry.RegisterForTenant(manager, tenant, respondIo.Value);

        // Remove schedules whose tenant is gone or inactive.
        using IStorageConnection connection = storage.GetConnection();
        foreach (RecurringJobDto job in connection.GetRecurringJobs())
        {
            int sep = job.Id.LastIndexOf(':');
            if (sep < 0) continue;
            string code = job.Id[(sep + 1)..];
            if (!active.ContainsKey(code))
            {
                manager.RemoveIfExists(job.Id);
                logger.LogInformation("Removed recurring job {JobId} (tenant inactive)", job.Id);
            }
        }

        logger.LogInformation("Recurring jobs reconciled for {Count} active tenant(s)", active.Count);
    }
}
