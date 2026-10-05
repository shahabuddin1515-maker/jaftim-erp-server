using System.Data;
using Dapper;
using Jaftim.Application.Messaging;
using Jaftim.Application.Modules.Email;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// The lease procedures every outbox table has (database/v2/007_Messaging_Outbox.sql): {Prefix}_ClaimById, _ClaimDue,
/// _MarkFailed, _Purge. None declares the audit trio - they run in the background, not as a user.
/// </summary>
public abstract class OutboxRepositoryBase<TItem>(IDbExecutor db, string prefix) : IOutboxStore<TItem>
    where TItem : class, IOutboxItem
{
    private const int ImmediateLeaseSeconds = 300;

    protected IDbExecutor Db { get; } = db;

    public Task<TItem?> ClaimAsync(long outboxId, Guid? leaseId, CancellationToken ct = default) =>
        Db.QueryFirstOrDefaultAsync<TItem>(
            SpCall.Procedure($"{prefix}_ClaimById")
                .With("@OutboxId", outboxId)
                .With("@LeaseId", leaseId, DbType.Guid)
                .With("@LeaseSeconds", ImmediateLeaseSeconds)
                .WithoutAudit(), ct);

    public Task<IReadOnlyList<OutboxLease>> ClaimDueAsync(int batchSize, int leaseSeconds, int minAgeSeconds, CancellationToken ct = default) =>
        Db.QueryAsync<OutboxLease>(
            SpCall.Procedure($"{prefix}_ClaimDue")
                .With("@BatchSize", batchSize)
                .With("@LeaseSeconds", leaseSeconds)
                .With("@MinAgeSeconds", minAgeSeconds)
                .WithoutAudit(), ct);

    public abstract Task<bool> MarkSucceededAsync(long outboxId, Guid leaseId, long? resultId, CancellationToken ct = default);

    public async Task<bool> MarkFailedAsync(long outboxId, Guid leaseId, string error, bool deadLetter, int retryDelaySeconds, CancellationToken ct = default) =>
        await Db.QuerySingleAsync<int>(
            SpCall.Procedure($"{prefix}_MarkFailed")
                .With("@OutboxId", outboxId)
                .With("@LeaseId", leaseId, DbType.Guid)
                .With("@LastError", error, DbType.String, 2000)
                .With("@DeadLetter", deadLetter)
                .With("@RetryDelaySeconds", retryDelaySeconds)
                .WithoutAudit(), ct) > 0;

    public Task<int> PurgeAsync(int retainDays, CancellationToken ct = default) =>
        Db.QuerySingleAsync<int>(
            SpCall.Procedure($"{prefix}_Purge").With("@RetainDays", retainDays).WithoutAudit(), ct);
}

public sealed class NotificationOutboxRepository(IDbExecutor db)
    : OutboxRepositoryBase<NotificationOutboxItem>(db, "NotificationOutbox"), INotificationOutboxRepository
{
    public Task<long> EnqueueAsync(NotificationRequest request, string payloadJson, int maxAttempts, CancellationToken ct = default) =>
        Db.QuerySingleAsync<long>(
            SpCall.Procedure("NotificationOutbox_Enqueue")
                .With("@TypeCode", request.TypeCode, DbType.AnsiString, 80)
                .With("@EntityType", request.EntityType, DbType.String, 120)
                .With("@EntityId", request.EntityId)
                .With("@PayloadJson", payloadJson, DbType.String)
                .With("@MaxAttempts", maxAttempts), ct);

    public Task<NotificationCreateResult> PersistAsync(NotificationOutboxItem item, NotificationRequest request, NotificationActor actor, CancellationToken ct = default)
    {
        // Explicit trio (ARCHITECTURE.md "Infrastructure"): the actor is the user whose request raised the event,
        // captured on the outbox row - not whoever runs the pipeline. Notification_Create's own BEGIN/COMMIT nests
        // inside this transaction, so its insert only becomes durable together with MarkPersisted.
        SpCall create = SpCall.Procedure("Notification_Create")
            .With("@TypeCode", request.TypeCode, DbType.AnsiString, 80)
            .With("@Title", request.Title, DbType.String, 200)
            .With("@Message", request.Message, DbType.String)
            .With("@EntityType", request.EntityType, DbType.String, 60)
            .With("@EntityId", request.EntityId)
            .With("@Url", request.Url, DbType.String, 400)
            .With("@Priority", request.Priority, DbType.Byte)
            .With("@RecipientUserIdsCsv", Csv(request.RecipientUserIds), DbType.String)
            .With("@RecipientRoleIdsCsv", Csv(request.RecipientRoleIds), DbType.String)
            .With("@ExcludeUserIdsCsv", Csv(request.ExcludeUserIds), DbType.String)
            .With("@UseRoleMap", request.UseRoleMap)
            .With("@ExcludeCreator", request.ExcludeCreator)
            .With("@CreatedBy", actor.UserProfileId, DbType.Int64)
            .With("@CreatedAt", actor.OccurredAtUtc, DbType.DateTime)
            .With("@CompanyId", actor.CompanyId, DbType.Int64)
            .WithoutAudit();

        return Db.InTransactionAsync(async tx =>
        {
            NotificationCreateResult result = await tx.QueryMultipleAsync(create, async grid =>
            {
                NotificationPayload payload = await grid.ReadSingleAsync<NotificationPayload>();
                IReadOnlyList<long> recipients = (await grid.ReadAsync<long>()).AsList();
                return new NotificationCreateResult(payload, recipients);
            }, ct);

            int marked = await tx.QueryFirstOrDefaultAsync<int>(
                SpCall.Procedure("NotificationOutbox_MarkPersisted")
                    .With("@OutboxId", item.OutboxId)
                    .With("@LeaseId", item.LeaseId, DbType.Guid)
                    .With("@NotificationId", result.Payload.NotificationId)
                    .WithoutAudit(), ct);
            if (marked == 0)
                throw new InvalidOperationException($"Notification outbox {item.OutboxId}: lease lost before persisting; rolled back.");
            return result;
        }, ct: ct);
    }

    public override async Task<bool> MarkSucceededAsync(long outboxId, Guid leaseId, long? resultId, CancellationToken ct = default) =>
        await Db.QuerySingleAsync<int>(
            SpCall.Procedure("NotificationOutbox_MarkSucceeded")
                .With("@OutboxId", outboxId)
                .With("@LeaseId", leaseId, DbType.Guid)
                .With("@NotificationId", resultId)
                .WithoutAudit(), ct) > 0;

    private static string? Csv(IReadOnlyCollection<long>? ids) =>
        ids is { Count: > 0 } ? string.Join(",", ids) : null;
}

public sealed class EmailOutboxRepository(IDbExecutor db)
    : OutboxRepositoryBase<EmailOutboxItem>(db, "EmailOutbox"), IEmailOutboxRepository
{
    public Task<long> EnqueueAsync(EmailMessage message, int maxAttempts, CancellationToken ct = default) =>
        Db.QuerySingleAsync<long>(
            SpCall.Procedure("EmailOutbox_Enqueue")
                .With("@ToAddress", message.To, DbType.String, 320)
                .With("@Subject", message.Subject, DbType.String, 400)
                .With("@HtmlBody", message.HtmlBody, DbType.String)
                .With("@Category", message.Category, DbType.String, 100)
                .With("@MaxAttempts", maxAttempts), ct);

    public override async Task<bool> MarkSucceededAsync(long outboxId, Guid leaseId, long? resultId, CancellationToken ct = default) =>
        await Db.QuerySingleAsync<int>(
            SpCall.Procedure("EmailOutbox_MarkSucceeded")
                .With("@OutboxId", outboxId)
                .With("@LeaseId", leaseId, DbType.Guid)
                .WithoutAudit(), ct) > 0;
}
