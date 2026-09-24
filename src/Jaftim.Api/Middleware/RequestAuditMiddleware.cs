using System.Threading.Channels;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Users;

namespace Jaftim.Api.Middleware;

/// <summary>
/// Preserves the legacy per-request audit (BaseController -> SaveActionURL) without paying a DB round-trip on the
/// request path: the middleware drops (path, user) into a bounded channel and <see cref="RequestAuditWriter"/>
/// flushes it in the background.
/// </summary>
public sealed class RequestAuditMiddleware(RequestDelegate next, Channel<RequestAuditEntry> channel)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUser user, ITenantContext tenant)
    {
        await next(context);

        if (user.IsAuthenticated && tenant.HasTenant && context.Request.Path.StartsWithSegments("/api"))
            channel.Writer.TryWrite(new RequestAuditEntry(context.Request.Path.Value!.ToLowerInvariant(), user.UserProfileId, tenant.TenantId));
    }
}

public sealed record RequestAuditEntry(string Path, long UserProfileId, int TenantId);

public sealed class RequestAuditWriter(Channel<RequestAuditEntry> channel, IServiceScopeFactory scopes, ILogger<RequestAuditWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (RequestAuditEntry entry in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(entry.TenantId, string.Empty);
                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                await users.SaveActionUrlAsync(entry.Path, entry.UserProfileId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Request audit write failed for {Path}", entry.Path);
            }
        }
    }

    public static Channel<RequestAuditEntry> CreateChannel() =>
        Channel.CreateBounded<RequestAuditEntry>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
}
