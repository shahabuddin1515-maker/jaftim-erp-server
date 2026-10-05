using Jaftim.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jaftim.Application.Messaging;

/// <summary>
/// The outbox pipelines (notifications, email). A producer inserts a row and signals; a processor claims the row under a
/// lease, runs it through the pipeline's steps and marks it succeeded, failed-with-retry or dead-lettered. The row is
/// the queue and the record - see docs/ARCHITECTURE.md "Messaging and job pipelines".
/// </summary>
public enum OutboxKind
{
    Notification,
    Email,
}

/// <summary>Columns every outbox row carries for the processor.</summary>
public interface IOutboxItem
{
    long OutboxId { get; }
    Guid LeaseId { get; }
    /// <summary>Claims so far, including the current one (a crash mid-run counts as an attempt).</summary>
    int AttemptCount { get; }
    int MaxAttempts { get; }
}

/// <summary>What the sweep gets back from a batch claim: the row and the lease that now owns it.</summary>
public sealed record OutboxLease(long OutboxId, Guid LeaseId);

/// <summary>One outbox table's claim/settle procedures. Every method is lease-guarded inside the procedure.</summary>
public interface IOutboxStore<TItem> where TItem : class, IOutboxItem
{
    /// <summary>
    /// <paramref name="leaseId"/> null: claim this row if it is pending, due and unleased (the immediate path).
    /// Otherwise: return it only while that lease still owns it (the sweep already claimed it). Null = nothing to do.
    /// </summary>
    Task<TItem?> ClaimAsync(long outboxId, Guid? leaseId, CancellationToken ct = default);
    /// <summary>Leases a batch of due rows older than <paramref name="minAgeSeconds"/> (the immediate path gets them first).</summary>
    Task<IReadOnlyList<OutboxLease>> ClaimDueAsync(int batchSize, int leaseSeconds, int minAgeSeconds, CancellationToken ct = default);
    /// <summary>Returns false when the lease was lost (expired and re-claimed) - the row is someone else's now.</summary>
    Task<bool> MarkSucceededAsync(long outboxId, Guid leaseId, long? resultId, CancellationToken ct = default);
    /// <summary>Dead-letters the row, or puts it back to pending due in <c>retryDelaySeconds</c> (ignored when dead-lettering).</summary>
    Task<bool> MarkFailedAsync(long outboxId, Guid leaseId, string error, bool deadLetter, int retryDelaySeconds, CancellationToken ct = default);
    /// <summary>Hard-deletes succeeded rows older than <paramref name="retainDays"/>. Dead letters are kept.</summary>
    Task<int> PurgeAsync(int retainDays, CancellationToken ct = default);
}

/// <summary>Wakes the processor for a row just inserted. Best-effort: the sweep catches anything a signal misses.</summary>
public interface IOutboxSignal
{
    void Enqueued(OutboxKind kind, long outboxId);
}

/// <summary>A failure that retrying cannot fix (bad address, unknown notification type, malformed payload).</summary>
public sealed class PermanentDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Decides whether a delivery failure is worth retrying. Infrastructure knows SqlException / SMTP codes.</summary>
public interface IDeliveryFailureClassifier
{
    bool IsPermanent(Exception exception);
}

public sealed record OutboxFailureDecision(bool DeadLetter, int RetryDelaySeconds);

public static class OutboxRetryPolicy
{
    /// <summary>Back-off after attempt 1, 2, 3, ...; the last entry repeats.</summary>
    public static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3),
    ];

    public static OutboxFailureDecision Decide(int attemptCount, int maxAttempts, bool permanent)
    {
        if (permanent || attemptCount >= maxAttempts) return new OutboxFailureDecision(true, 0);
        TimeSpan delay = Delays[Math.Clamp(attemptCount - 1, 0, Delays.Length - 1)];
        return new OutboxFailureDecision(false, (int)delay.TotalSeconds);
    }
}

/// <summary>A pipeline context built from one outbox row.</summary>
public interface IOutboxDeliveryContext
{
    /// <summary>Id of what the delivery produced (the Notification row), stored on the outbox row for tracing.</summary>
    long? ResultId { get; }
}

public enum OutboxProcessResult
{
    /// <summary>Not pending, not due, or leased by someone else - another run owns it or it is already done.</summary>
    NotClaimed,
    Succeeded,
    RetryScheduled,
    DeadLettered,
}

/// <summary>Claim -> run the pipeline -> settle. The same for every outbox; subclasses only map a row to a context.</summary>
public abstract class OutboxProcessor<TItem, TContext>(
    IOutboxStore<TItem> store,
    Pipeline<TContext> pipeline,
    IDeliveryFailureClassifier classifier,
    ILogger logger)
    where TItem : class, IOutboxItem
    where TContext : IOutboxDeliveryContext
{
    protected abstract OutboxKind Kind { get; }

    /// <summary>Throw <see cref="PermanentDeliveryException"/> when the row cannot be turned into a context.</summary>
    protected abstract TContext CreateContext(TItem item);

    public async Task<OutboxProcessResult> ProcessAsync(long outboxId, Guid? leaseId, CancellationToken ct)
    {
        TItem? item = await store.ClaimAsync(outboxId, leaseId, ct);
        if (item is null)
        {
            logger.LogDebug("{Kind} outbox {OutboxId} not claimed (done, not due, or leased elsewhere)", Kind, outboxId);
            return OutboxProcessResult.NotClaimed;
        }

        TContext context;
        try
        {
            context = CreateContext(item);
            await pipeline.RunAsync(context, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // On cancellation (host shutdown) nothing is settled: the lease expires and the sweep re-claims the row.
            OutboxFailureDecision decision = OutboxRetryPolicy.Decide(item.AttemptCount, item.MaxAttempts, classifier.IsPermanent(ex));
            if (decision.DeadLetter)
                logger.LogError(ex, "{Kind} outbox {OutboxId} dead-lettered after attempt {Attempt}", Kind, item.OutboxId, item.AttemptCount);
            else
                logger.LogWarning(ex, "{Kind} outbox {OutboxId} attempt {Attempt} failed; retry in {Delay}s", Kind, item.OutboxId, item.AttemptCount, decision.RetryDelaySeconds);

            await store.MarkFailedAsync(item.OutboxId, item.LeaseId, Describe(ex), decision.DeadLetter, decision.RetryDelaySeconds, CancellationToken.None);
            return decision.DeadLetter ? OutboxProcessResult.DeadLettered : OutboxProcessResult.RetryScheduled;
        }

        if (!await store.MarkSucceededAsync(item.OutboxId, item.LeaseId, context.ResultId, CancellationToken.None))
            logger.LogWarning("{Kind} outbox {OutboxId} delivered but its lease had expired; it may be delivered again", Kind, item.OutboxId);
        return OutboxProcessResult.Succeeded;
    }

    private static string Describe(Exception ex)
    {
        string text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= 2000 ? text : text[..2000];
    }
}
