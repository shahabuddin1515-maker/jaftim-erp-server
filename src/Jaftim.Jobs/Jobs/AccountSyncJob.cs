using Hangfire;
using Jaftim.Application.Jobs;
using Jaftim.Application.Modules.Users;
using Jaftim.Infrastructure.Repositories.Catalog;

namespace Jaftim.Jobs.Jobs;

/// <summary>
/// Tenant AspNetUsers + UserProfile -> catalog Account + AccountTenant (database/catalog/002_Seed_From_Tenant.md).
/// Picks up customers created by the ingestion procedures and legacy-app password changes; for accounts whose
/// password was last set through the API the direction reverses and the catalog hash is pushed down to the
/// tenant row. Idempotent; a few thousand rows per tenant per minute is cheap.
/// </summary>
public sealed class AccountSyncJob(
    IJobRunner jobs,
    IUserRepository users,
    IAccountSyncRepository catalog,
    ILogger<AccountSyncJob> logger)
{
    public const string Id = "account-sync";
    public const string Cron = "* * * * *";

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(string tenantCode, CancellationToken ct)
    {
        var job = new JobContext(Id, tenantCode);
        return jobs.RunAsync(job, async c =>
        {
            int tenantId = job.TenantId ?? throw new InvalidOperationException("Tenant was not bound.");
            IReadOnlyList<TenantCredentialRow> rows = await users.GetCredentialRowsForSyncAsync(c);

            int mirrored = 0, failed = 0;
            foreach (TenantCredentialRow row in rows)
            {
                try
                {
                    AccountUpsertResult result = await catalog.UpsertFromTenantAsync(row, tenantId, c);
                    if (result.MirrorToTenant && result.CatalogPasswordHash is not null)
                    {
                        await users.MirrorPasswordHashAsync(row.Id, result.CatalogPasswordHash, c);
                        mirrored++;
                    }
                }
                catch (Exception ex) when (!c.IsCancellationRequested)
                {
                    failed++;
                    logger.LogWarning(ex, "Account sync failed for tenant {Tenant} user {UserProfileId}", tenantCode, row.UserProfileId);
                }
            }

            logger.LogInformation("Account sync for {Tenant}: {Rows} rows, {Mirrored} hashes mirrored to tenant, {Failed} failed", tenantCode, rows.Count, mirrored, failed);
        }, ct);
    }
}
