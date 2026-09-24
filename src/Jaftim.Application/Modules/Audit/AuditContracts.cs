using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Audit;

namespace Jaftim.Application.Modules.Audit;

/// <summary>Everything the audit writer captured for one write, including the request context it ran in.</summary>
public sealed record AuditRecord(
    int TenantId,
    DateTime AtUtc,
    long? ActorUserProfileId,
    string? ActorAccountId,
    string Action,
    string EntityType,
    long? EntityId,
    string? BeforeJson,
    string? AfterJson,
    Guid? CorrelationId,
    string? Ip,
    string? UserAgent);

/// <summary>Tenant AuditLog_* procedures.</summary>
public interface IAuditLogRepository
{
    Task WriteAsync(AuditRecord record, CancellationToken ct = default);
    Task<PagedResult<AuditEntry>> GetByEntityAsync(string entityType, long? entityId, PagedRequest paging, CancellationToken ct = default);
}

public interface IAuditQueryService
{
    Task<PagedResult<AuditEntry>> GetByEntityAsync(string entityType, long? entityId, PagedRequest paging, CancellationToken ct = default);
}

public sealed class AuditQueryService(IAuditLogRepository repository) : IAuditQueryService
{
    public Task<PagedResult<AuditEntry>> GetByEntityAsync(string entityType, long? entityId, PagedRequest paging, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityType) || entityType.Length > 100)
            throw new Domain.Exceptions.BusinessRuleException("entityType is required (max 100 characters).");
        return repository.GetByEntityAsync(entityType.Trim(), entityId, paging, ct);
    }
}
