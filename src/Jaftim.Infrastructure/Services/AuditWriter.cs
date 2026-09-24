using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jaftim.Infrastructure.Services;

/// <summary>Request metadata the audit writer stamps on each record. The API fills it from HttpContext; Jobs leave it empty.</summary>
public interface IAuditRequestContext
{
    Guid? CorrelationId { get; }
    string? Ip { get; }
    string? UserAgent { get; }
}

public sealed class EmptyAuditRequestContext : IAuditRequestContext
{
    public Guid? CorrelationId => null;
    public string? Ip => null;
    public string? UserAgent => null;
}

/// <summary>
/// Captures actor/tenant/request context synchronously (they are scoped and gone after the request) and queues the
/// record; <see cref="AuditFlushService"/> writes it to the tenant AuditLog in the background. Best-effort by design.
/// </summary>
public sealed class ChannelAuditWriter(
    Channel<AuditRecord> channel,
    ICurrentUser user,
    ITenantContext tenant,
    IAuditRequestContext request,
    IDateTimeProvider clock,
    ILogger<ChannelAuditWriter> logger) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
    {
        if (!tenant.HasTenant)
        {
            logger.LogWarning("Audit {Action} on {EntityType}/{EntityId} dropped: no tenant bound", action, entityType, entityId);
            return ValueTask.CompletedTask;
        }

        var record = new AuditRecord(
            tenant.TenantId,
            clock.UtcNow,
            user.IsAuthenticated ? user.UserProfileId : null,
            user.AccountId,
            action,
            entityType,
            entityId,
            before is null ? null : JsonSerializer.Serialize(before, Json),
            after is null ? null : JsonSerializer.Serialize(after, Json),
            request.CorrelationId,
            request.Ip,
            request.UserAgent);

        if (!channel.Writer.TryWrite(record))
            logger.LogWarning("Audit channel full; dropped {Action} on {EntityType}/{EntityId}", action, entityType, entityId);
        return ValueTask.CompletedTask;
    }

    public static Channel<AuditRecord> CreateChannel() =>
        Channel.CreateBounded<AuditRecord>(new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest });
}

/// <summary>Drains the audit channel; binds each record's tenant on a fresh scope before writing.</summary>
public sealed class AuditFlushService(Channel<AuditRecord> channel, IServiceScopeFactory scopes, ILogger<AuditFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (AuditRecord record in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(record.TenantId, string.Empty);
                await scope.ServiceProvider.GetRequiredService<IAuditLogRepository>().WriteAsync(record, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Audit write failed for {Action} on {EntityType}/{EntityId} (tenant {TenantId})", record.Action, record.EntityType, record.EntityId, record.TenantId);
            }
        }
    }
}
