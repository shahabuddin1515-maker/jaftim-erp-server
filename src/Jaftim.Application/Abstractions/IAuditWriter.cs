namespace Jaftim.Application.Abstractions;

/// <summary>
/// Central business audit (tenant AuditLog). Services call it after every write they perform: who, what, when,
/// before/after. Delivery is asynchronous and best-effort (bounded channel + background flush) so it never adds
/// latency or failure to the business action; the actor, tenant and request metadata are captured at call time.
/// </summary>
public interface IAuditWriter
{
    /// <param name="action">Dotted verb, e.g. "UserRole.Assigned", "Stock.StatusChanged".</param>
    /// <param name="entityType">e.g. "UserProfile", "Stock".</param>
    /// <param name="entityId">Primary key of the entity, or null when the action has no single subject.</param>
    /// <param name="before">Snapshot before the change (serialised to JSON), or null for creates.</param>
    /// <param name="after">Snapshot after the change (serialised to JSON), or null for deletes.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default);
}
