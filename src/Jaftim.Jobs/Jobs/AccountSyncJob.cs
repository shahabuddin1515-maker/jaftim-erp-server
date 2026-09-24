using Hangfire;
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
    TenantScopeRunner tenants,
    IUserRepository users,
    IAccountSyncRepository catalog,
    ILogger<AccountSyncJob> logger)
{
    public const string Id = "account-sync";
    public const string Cron = "* * * * *";

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(string tenantCode, CancellationToken ct)
    {
        var tenant = await tenants.BindAsync(tenantCode, ct);
        IReadOnlyList<TenantCredentialRow> rows = await users.GetCredentialRowsForSyncAsync(ct);

        int mirrored = 0, failed = 0;
        foreach (TenantCredentialRow row in rows)
        {
            try
            {
                AccountUpsertResult result = await catalog.UpsertFromTenantAsync(row, tenant.TenantId, ct);
                if (result.MirrorToTenant && result.CatalogPasswordHash is not null)
                {
                    await users.MirrorPasswordHashAsync(row.Id, result.CatalogPasswordHash, ct);
                    mirrored++;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(ex, "Account sync failed for tenant {Tenant} user {UserProfileId}", tenantCode, row.UserProfileId);
            }
        }

        logger.LogInformation("Account sync for {Tenant}: {Rows} rows, {Mirrored} hashes mirrored to tenant, {Failed} failed", tenantCode, rows.Count, mirrored, failed);
    }
}
