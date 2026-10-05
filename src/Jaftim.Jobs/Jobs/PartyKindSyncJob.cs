using Hangfire;
using Jaftim.Application.Jobs;
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
public sealed class PartyKindSyncJob(IJobRunner jobs, IDbExecutor db, ILogger<PartyKindSyncJob> logger)
{
    public const string Id = "party-kind-sync";
    public const string Cron = "*/5 * * * *";

    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    [AutomaticRetry(Attempts = 0)]   // a missed run is picked up by the next tick
    public Task RunAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(Id, tenantCode), async c =>
        {
            int kinds = await db.QuerySingleAsync<int>(
                SpCall.Procedure("Party_ReconcileKinds").WithoutAudit().WithTimeout(120), c);
            int links = await db.QuerySingleAsync<int>(
                SpCall.Procedure("Inquiry_ReconcileUserProfileId").WithoutAudit().WithTimeout(120), c);

            if (kinds > 0 || links > 0)
                logger.LogInformation("Party sync for {Tenant}: {Kinds} classification(s), {Links} inquiry link(s) updated", tenantCode, kinds, links);
        }, ct);
}
