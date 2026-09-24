namespace Jaftim.Domain.Entities.Audit;

/// <summary>tenant dbo.AuditLog row (AuditLog_GetByEntity adds ActorName).</summary>
public sealed class AuditEntry
{
    public long AuditId { get; set; }
    public DateTime AtUtc { get; set; }
    public long? ActorUserProfileId { get; set; }
    public string? ActorName { get; set; }
    public string? ActorAccountId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public long? EntityId { get; set; }
    /// <summary>JSON snapshot before the change (null for creates).</summary>
    public string? Before { get; set; }
    /// <summary>JSON snapshot after the change (null for deletes).</summary>
    public string? After { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}
