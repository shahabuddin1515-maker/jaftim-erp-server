using Hangfire;
using Jaftim.Application.Jobs;
using Microsoft.Extensions.Options;

namespace Jaftim.Jobs.Jobs;

/// <summary>Bound from "RespondIO" - same keys as RespondIOSync/appsettings.json so the config can be copied across.</summary>
public sealed class RespondIoOptions
{
    public const string SectionName = "RespondIO";
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool EnableContactSync { get; set; }
    public bool EnableCustomerPush { get; set; }
    public bool EnableSyncStatusWriteBack { get; set; }
    public bool EnableConversationSync { get; set; }
    public int IntervalInHours { get; set; } = 1;
    public int ConversationSyncIntervalHours { get; set; } = 24;
    public int MaxContactsPerCycle { get; set; }
    public int ProcessChunkSize { get; set; } = 50;
    public int MaxProcessPassesPerCycle { get; set; } = 60;
}

/// <summary>
/// Replaces the three RespondIOSync hosted services. Each is a recurring job gated by its Enable* switch, exactly as
/// before, and each stage stays independently re-runnable (README section 6.5). Every stage runs the existing
/// tpi_respondio_* procedures - the inquiry/customer pipeline itself (InquirySave_FromLead -> CustomerSaveInternal)
/// is NOT reimplemented.
///
/// STATUS: skeletons. Port the worker bodies from RespondIOSync/*.cs in the Integrations phase. Key gotchas to carry
/// over: 600 s command timeout on tpi_respondio_contactsync_process, chunked processing, erp_sync_status is a
/// case-sensitive list field, a 400 from contact lookup is FAILED not "absent", cutoff missing = start from now.
/// </summary>
public sealed class RespondIoContactSyncJob(IJobRunner jobs, IOptions<RespondIoOptions> options, ILogger<RespondIoContactSyncJob> logger)
{
    public const string Id = "respondio-contact-sync";

    [Queue("integrations")]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(Id, tenantCode), _ =>
        {
            if (!options.Value.EnableContactSync) { logger.LogDebug("Respond.io contact sync disabled"); return Task.CompletedTask; }
            throw new NotImplementedException("Port RespondIOSync/RespondIOContactSyncWorker.cs (fetch/dump -> process -> status write-back).");
        }, ct);
}

public sealed class RespondIoCustomerPushJob(IJobRunner jobs, IOptions<RespondIoOptions> options, ILogger<RespondIoCustomerPushJob> logger)
{
    public const string Id = "respondio-customer-push";

    [Queue("integrations")]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(Id, tenantCode), _ =>
        {
            if (!options.Value.EnableCustomerPush) { logger.LogDebug("Respond.io customer push disabled"); return Task.CompletedTask; }
            throw new NotImplementedException("Port RespondIOSync/RespondIOCustomerPushWorker.cs (collect -> lookup-before-create -> save).");
        }, ct);
}

public sealed class RespondIoConversationSyncJob(IJobRunner jobs, IOptions<RespondIoOptions> options, ILogger<RespondIoConversationSyncJob> logger)
{
    public const string Id = "respondio-conversation-sync";

    [Queue("integrations")]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(Id, tenantCode), _ =>
        {
            if (!options.Value.EnableConversationSync) { logger.LogDebug("Respond.io conversation sync disabled"); return Task.CompletedTask; }
            throw new NotImplementedException("Port RespondIOSync/RespondIOConversationSyncWorker.cs (messages + attachments to Blob).");
        }, ct);
}
