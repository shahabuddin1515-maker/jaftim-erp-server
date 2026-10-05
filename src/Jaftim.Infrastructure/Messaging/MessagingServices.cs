using System.Net.Mail;
using System.Text.Json;
using Hangfire;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Jobs;
using Jaftim.Application.Messaging;
using Jaftim.Application.Modules.Email;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Jaftim.Infrastructure.Messaging;

/// <summary>What retrying cannot fix. Everything else (timeouts, deadlocks, SMTP 4xx, connection loss) is retried.</summary>
public sealed class DeliveryFailureClassifier : IDeliveryFailureClassifier
{
    public bool IsPermanent(Exception exception) => exception switch
    {
        PermanentDeliveryException => true,
        AppException => true,
        // RAISERROR/THROW business rules, e.g. Notification_Create's "Unknown or inactive notification type code".
        SqlException sql => sql.Number >= 50000,
        // 550/551/553: the mailbox does not exist or is refused - the same answer every time.
        SmtpFailedRecipientException smtp => smtp.StatusCode is SmtpStatusCode.MailboxUnavailable
            or SmtpStatusCode.UserNotLocalTryAlternatePath or SmtpStatusCode.MailboxNameNotAllowed,
        FormatException or JsonException => true,
        _ => false,
    };
}

/// <summary>
/// The immediate path: enqueues the per-message Hangfire job right after the row is inserted. If the tenant code is
/// unknown in this scope or Hangfire is unreachable, nothing is lost - the per-tenant sweep claims the row within a
/// minute or two.
/// </summary>
public sealed class HangfireOutboxSignal(IJobScheduler scheduler, ITenantContext tenant, ILogger<HangfireOutboxSignal> logger) : IOutboxSignal
{
    public void Enqueued(OutboxKind kind, long outboxId)
    {
        if (string.IsNullOrEmpty(tenant.TenantCode))
        {
            logger.LogDebug("{Kind} outbox {OutboxId}: no tenant code in scope; left to the sweep", kind, outboxId);
            return;
        }

        string code = tenant.TenantCode;
        try
        {
            _ = kind switch
            {
                OutboxKind.Notification => scheduler.Enqueue<NotificationOutboxJobs>(
                    j => j.ProcessAsync(code, outboxId, null, CancellationToken.None), JobQueues.Notifications),
                OutboxKind.Email => scheduler.Enqueue<EmailOutboxJobs>(
                    j => j.ProcessAsync(code, outboxId, null, CancellationToken.None), JobQueues.Email),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }
        catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
        {
            logger.LogWarning(ex, "{Kind} outbox {OutboxId}: immediate dispatch failed; left to the sweep", kind, outboxId);
        }
    }
}

/// <summary>Sweep settings shared by both outboxes.</summary>
internal static class OutboxSweep
{
    public const string Cron = "* * * * *";
    public const int BatchSize = 50;
    /// <summary>Long enough for a queued job to be picked up; on expiry the row is simply re-claimed.</summary>
    public const int LeaseSeconds = 600;
    /// <summary>Rows younger than this belong to the immediate path.</summary>
    public const int MinAgeSeconds = 30;
    public const int RetainDays = 14;
}

/// <summary>
/// Notification pipeline jobs. <see cref="ProcessAsync"/> runs on the "notifications" queue, which ONLY the API host
/// consumes (realtime push needs its SignalR hub); <see cref="SweepAsync"/> is a per-tenant recurring job in the Jobs
/// host. Lives in Infrastructure because both hosts must be able to load it.
/// </summary>
public sealed class NotificationOutboxJobs(
    IJobRunner jobs,
    NotificationOutboxProcessor processor,
    INotificationOutboxRepository outbox,
    IJobScheduler scheduler,
    ILogger<NotificationOutboxJobs> logger)
{
    public const string SweepId = "notification-outbox-sweep";
    public const string SweepCron = OutboxSweep.Cron;

    [Queue(JobQueues.Notifications)]
    [AutomaticRetry(Attempts = 0)] // retries belong to the outbox (NextAttemptAtUtc + back-off), not Hangfire
    public Task ProcessAsync(string tenantCode, long outboxId, Guid? leaseId, CancellationToken ct) =>
        jobs.RunAsync(new JobContext("notification-outbox-process", tenantCode),
            c => processor.ProcessAsync(outboxId, leaseId, c), ct);

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    [AutomaticRetry(Attempts = 0)]
    public Task SweepAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(SweepId, tenantCode), async c =>
        {
            IReadOnlyList<OutboxLease> due = await outbox.ClaimDueAsync(OutboxSweep.BatchSize, OutboxSweep.LeaseSeconds, OutboxSweep.MinAgeSeconds, c);
            foreach (OutboxLease lease in due)
                scheduler.Enqueue<NotificationOutboxJobs>(j => j.ProcessAsync(tenantCode, lease.OutboxId, lease.LeaseId, CancellationToken.None), JobQueues.Notifications);

            int purged = await outbox.PurgeAsync(OutboxSweep.RetainDays, c);
            if (due.Count > 0 || purged > 0)
                logger.LogInformation("Notification outbox sweep: {Due} re-dispatched, {Purged} purged", due.Count, purged);
        }, ct);
}

/// <summary>Email pipeline jobs, both in the Jobs host ("email" queue / per-tenant recurring sweep).</summary>
public sealed class EmailOutboxJobs(
    IJobRunner jobs,
    EmailOutboxProcessor processor,
    IEmailOutboxRepository outbox,
    IJobScheduler scheduler,
    ILogger<EmailOutboxJobs> logger)
{
    public const string SweepId = "email-outbox-sweep";
    public const string SweepCron = OutboxSweep.Cron;

    [Queue(JobQueues.Email)]
    [AutomaticRetry(Attempts = 0)] // retries belong to the outbox (NextAttemptAtUtc + back-off), not Hangfire
    public Task ProcessAsync(string tenantCode, long outboxId, Guid? leaseId, CancellationToken ct) =>
        jobs.RunAsync(new JobContext("email-outbox-process", tenantCode),
            c => processor.ProcessAsync(outboxId, leaseId, c), ct);

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    [AutomaticRetry(Attempts = 0)]
    public Task SweepAsync(string tenantCode, CancellationToken ct) =>
        jobs.RunAsync(new JobContext(SweepId, tenantCode), async c =>
        {
            IReadOnlyList<OutboxLease> due = await outbox.ClaimDueAsync(OutboxSweep.BatchSize, OutboxSweep.LeaseSeconds, OutboxSweep.MinAgeSeconds, c);
            foreach (OutboxLease lease in due)
                scheduler.Enqueue<EmailOutboxJobs>(j => j.ProcessAsync(tenantCode, lease.OutboxId, lease.LeaseId, CancellationToken.None), JobQueues.Email);

            int purged = await outbox.PurgeAsync(OutboxSweep.RetainDays, c);
            if (due.Count > 0 || purged > 0)
                logger.LogInformation("Email outbox sweep: {Due} re-dispatched, {Purged} purged", due.Count, purged);
        }, ct);
}
