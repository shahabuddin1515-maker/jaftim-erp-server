using System.Text.Json;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace Jaftim.Application.Modules.Notifications;

/// <summary>
/// The notification pipeline:
/// <code>
///   NotifyAsync -> NotificationOutbox row (actor captured by the audit trio) -> signal
///   processor (API host, Hangfire queue "notifications"):
///     1 PersistNotificationStep  Notification_Create as the original actor, in one transaction with marking the
///                                row persisted (the commit point; retried on failure, never duplicated)
///     2 PushNotificationStep     SignalR to the recipients                    (best-effort, as in legacy)
/// </code>
/// Steps run in DI registration order (Application/DependencyInjection.cs).
/// </summary>
public static class NotificationPipeline
{
    /// <summary>A notification hours late is still worth delivering; beyond ~5 attempts (about 45 min) it is noise.</summary>
    public const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(NotificationRequest request) => JsonSerializer.Serialize(request, Json);

    public static NotificationRequest Deserialize(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationRequest>(payloadJson, Json)
                ?? throw new PermanentDeliveryException("Notification payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new PermanentDeliveryException("Notification payload is not a valid NotificationRequest.", ex);
        }
    }
}

/// <summary>Who raised the event and when - the audit trio Notification_Create is called with.</summary>
public sealed record NotificationActor(long? UserProfileId, long? CompanyId, DateTime OccurredAtUtc);

/// <summary>A claimed NotificationOutbox row (columns of NotificationOutbox_ClaimById).</summary>
public sealed class NotificationOutboxItem : IOutboxItem
{
    public long OutboxId { get; set; }
    public Guid LeaseId { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    /// <summary>The actor (UserProfileId) whose request raised the event.</summary>
    public long? CreatedBy { get; set; }
    public long? CompanyId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Set once step 1 has committed; a retry of such a row must not run Notification_Create again.</summary>
    public long? NotificationId { get; set; }
}

public interface INotificationOutboxRepository : IOutboxStore<NotificationOutboxItem>
{
    /// <summary>EXEC NotificationOutbox_Enqueue. The audit trio (current actor, UtcNow, company) is injected as usual.</summary>
    Task<long> EnqueueAsync(NotificationRequest request, string payloadJson, int maxAttempts, CancellationToken ct = default);

    /// <summary>
    /// ONE transaction: EXEC Notification_Create as <paramref name="actor"/> (its audit trio comes from the row, not the
    /// current scope - the procedure routes on @CreatedBy, and the pipeline runs long after, and in another host than,
    /// the request that raised the event), read its results, then NotificationOutbox_MarkPersisted under the row's
    /// lease. Any failure - including after the procedure's own commit, or a lost lease - rolls the notification back,
    /// so a retry can never duplicate it.
    /// </summary>
    Task<NotificationCreateResult> PersistAsync(NotificationOutboxItem item, NotificationRequest request, NotificationActor actor, CancellationToken ct = default);
}

public sealed class OutboxNotificationDispatcher(
    INotificationOutboxRepository outbox,
    IOutboxSignal signal,
    ILogger<OutboxNotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        try
        {
            long outboxId = await outbox.EnqueueAsync(request, NotificationPipeline.Serialize(request), NotificationPipeline.MaxAttempts, ct);
            signal.Enqueued(OutboxKind.Notification, outboxId);
        }
        catch (Exception ex)
        {
            // Preserved behaviour: a notification failure must never break the business action that raised it.
            logger.LogError(ex, "Notification {TypeCode} for {EntityType}/{EntityId} could not be queued", request.TypeCode, request.EntityType, request.EntityId);
        }
    }
}

/// <summary>The context the notification steps share for one outbox row.</summary>
public sealed class NotificationDelivery(NotificationOutboxItem item, NotificationRequest request) : IOutboxDeliveryContext
{
    public NotificationOutboxItem Item { get; } = item;
    public NotificationRequest Request { get; } = request;
    public NotificationActor Actor { get; } = new(item.CreatedBy, item.CompanyId, item.CreatedAtUtc);
    /// <summary>Set by <see cref="PersistNotificationStep"/>; null when an earlier attempt already persisted.</summary>
    public NotificationCreateResult? Result { get; set; }
    public long? ResultId => Result?.Payload.NotificationId ?? Item.NotificationId;
}

/// <summary>
/// Step 1: Notification_Create resolves the audience and stores the notification, atomically with marking the outbox
/// row persisted. Failures are retried. A row an earlier attempt already persisted is not persisted again - and not
/// pushed either (its payload is gone; the notification is in the inbox, and push is best-effort anyway).
/// </summary>
public sealed class PersistNotificationStep(INotificationOutboxRepository outbox, ILogger<PersistNotificationStep> logger) : IPipelineStep<NotificationDelivery>
{
    public async Task InvokeAsync(NotificationDelivery context, PipelineStepDelegate next, CancellationToken ct)
    {
        if (context.Item.NotificationId is { } existing)
            logger.LogInformation("Notification outbox {OutboxId} was already persisted as notification {NotificationId}", context.Item.OutboxId, existing);
        else
            context.Result = await outbox.PersistAsync(context.Item, context.Request, context.Actor, ct);
        await next(ct);
    }
}

/// <summary>
/// Step 2: realtime push. Best-effort - the notification is already in every recipient's inbox, so a push failure is
/// logged and never retried (a retry would re-run step 1 and duplicate the notification).
/// </summary>
public sealed class PushNotificationStep(INotificationPusher pusher, ILogger<PushNotificationStep> logger) : IPipelineStep<NotificationDelivery>
{
    public async Task InvokeAsync(NotificationDelivery context, PipelineStepDelegate next, CancellationToken ct)
    {
        if (context.Result is { RecipientUserIds.Count: > 0 } result)
        {
            try
            {
                await pusher.PushAsync(result.Payload, result.RecipientUserIds, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Realtime push of notification {NotificationId} failed; it is still in the inbox", result.Payload.NotificationId);
            }
        }
        await next(ct);
    }
}

public sealed class NotificationOutboxProcessor(
    INotificationOutboxRepository store,
    Pipeline<NotificationDelivery> pipeline,
    IDeliveryFailureClassifier classifier,
    ILogger<NotificationOutboxProcessor> logger)
    : OutboxProcessor<NotificationOutboxItem, NotificationDelivery>(store, pipeline, classifier, logger)
{
    protected override OutboxKind Kind => OutboxKind.Notification;

    protected override NotificationDelivery CreateContext(NotificationOutboxItem item) =>
        new(item, NotificationPipeline.Deserialize(item.PayloadJson));
}
