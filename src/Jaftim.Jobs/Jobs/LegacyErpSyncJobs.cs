using Hangfire;

namespace Jaftim.Jobs.Jobs;

/// <summary>
/// Replaces StockSync/Worker.cs + ImagesSyncWorker.cs (Azure Storage Queue consumers that POST to the legacy PHP
/// "Old ERP": /api/add-stock, /api/update-stock, /api/upload-stock-images with an X-API-KEY header).
///
/// In v2 the API enqueues these jobs directly (IJobScheduler) after StockSave / update / image upload for stock types
/// Jaftim (1) and Dealer (2) only - Customer Order and Dummy Stock are deliberately excluded (README section 8.7).
/// On Create, the Old ERP id returned must be written back via UpdateStockCode (Stock.StockCode).
///
/// Hangfire gives what the legacy worker lacked: visible failures, bounded retries with back-off, and no silent drop
/// after 5 attempts (failed jobs stay in the dashboard).
///
/// STATUS: skeleton. Port StockSync/Worker.cs body here in the Stock module phase (docs/REWRITE_PLAN.md).
/// While the legacy MVC app still produces to the Azure queues, run the legacy StockSync worker alongside; do not
/// consume the same queue from both.
/// </summary>
public sealed class LegacyErpStockSyncJob(TenantScopeRunner tenants, ILogger<LegacyErpStockSyncJob> logger)
{
    [Queue("legacy-sync")]
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [60, 180, 600, 1800, 3600])]
    public async Task SyncStockAsync(string tenantCode, long stockId, string operationType, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);
        logger.LogWarning("LegacyErpStockSyncJob not implemented yet: {Operation} stock {StockId}", operationType, stockId);
        throw new NotImplementedException("Port StockSync/Worker.cs (web_sync_get_stock_by_Id -> PHP ERP -> UpdateStockCode).");
    }

    [Queue("legacy-sync")]
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [60, 180, 600, 1800, 3600])]
    public async Task SyncImagesAsync(string tenantCode, long stockId, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);
        logger.LogWarning("LegacyErpStockSyncJob.SyncImages not implemented yet: stock {StockId}", stockId);
        throw new NotImplementedException("Port StockSync/ImagesSyncWorker.cs (web_sync_get_stock_images -> PHP ERP).");
    }
}
