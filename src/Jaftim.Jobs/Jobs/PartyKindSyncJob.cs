using Hangfire;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Jobs.Jobs;

/// <summary>
/// Keeps the normalized columns in step with whatever the paths this backend does NOT own have written:
///
///   * the lead pipeline (Azure Function -> InsertLead -> InquiryImport_FromLead -> InquirySave_FromLead ->
///     CustomerSaveInternal), which is deliberately untouched and creates parties and inquiries directly;
///   * the Respond.io sync procedures;
///   * the legacy MVC app.
///
/// Two reconciliations, both set-based and cheap (about 1k parties, 1.5k inquiries):
///   Party_ReconcileKinds          - recomputes UserProfile.PartyKind (Customer vs Contact); manual pins are skipped.
///   Inquiry_ReconcileUserProfileId - fills the real Inquiry -> UserProfile FK from the legacy AspNetUserId link.
///
/// The v2 API already recomputes synchronously on its own writes, so this is the safety net for everyone else.
/// </summary>
public sealed class PartyKindSyncJob(TenantScopeRunner tenants, IDbExecutor db, ILogger<PartyKindSyncJob> logger)
{
    public const string Id = "party-kind-sync";
    public const string Cron = "*/5 * * * *";

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    [AutomaticRetry(Attempts = 0)]   // a missed run is picked up by the next tick
    public async Task RunAsync(string tenantCode, CancellationToken ct)
    {
        await tenants.BindAsync(tenantCode, ct);

        int kinds = await db.QuerySingleAsync<int>(
            SpCall.Procedure("Party_ReconcileKinds").WithoutAudit().WithTimeout(120), ct);
        int links = await db.QuerySingleAsync<int>(
            SpCall.Procedure("Inquiry_ReconcileUserProfileId").WithoutAudit().WithTimeout(120), ct);

        if (kinds > 0 || links > 0)
            logger.LogInformation("Party sync for {Tenant}: {Kinds} classification(s), {Links} inquiry link(s) updated", tenantCode, kinds, links);
    }
}
