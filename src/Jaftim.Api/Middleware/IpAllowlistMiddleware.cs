using System.Net;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Users;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jaftim.Api.Middleware;

public sealed class IpAllowlistOptions
{
    public const string SectionName = "IpAllowlist";
    /// <summary>Off by default in Development. In UAT/Prod the legacy behaviour (office CIDRs only) should be ON.</summary>
    public bool Enabled { get; set; }
    /// <summary>Static CIDRs (the legacy BaseController.AllowedCidrs list). Combined with Base_WhitelistedIPs from the DB.</summary>
    public string[] Cidrs { get; set; } = [];
    /// <summary>Path prefixes exempt from the check (login, health, docs).</summary>
    public string[] ExemptPathPrefixes { get; set; } = ["/api/auth", "/health", "/openapi", "/scalar", "/hubs"];
}

/// <summary>
/// Port of the legacy BaseController IP allowlist: request IP must be inside a static CIDR OR a Base_WhitelistedIPs
/// CIDR, unless the user has UserProfile.RemoteAccessAllowed = 1. Runs after authentication so the bypass flag can be
/// read. Long term this belongs in App Service access restrictions / Front Door; it is here so behaviour is preserved.
/// </summary>
public sealed class IpAllowlistMiddleware(RequestDelegate next, IOptions<IpAllowlistOptions> options, IMemoryCache cache, ILogger<IpAllowlistMiddleware> logger)
{
    private readonly IpAllowlistOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, IUserRepository users)
    {
        if (!_options.Enabled || !currentUser.IsAuthenticated || IsExempt(context.Request.Path))
        {
            await next(context);
            return;
        }

        IPAddress? ip = context.Connection.RemoteIpAddress;
        if (ip is not null && await IsAllowedAsync(ip, currentUser, users, context.RequestAborted))
        {
            await next(context);
            return;
        }

        logger.LogWarning("IP {Ip} blocked for user {UserProfileId}", ip, currentUser.UserProfileId);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { title = "Access from this network is not allowed.", status = 403 });
    }

    private bool IsExempt(PathString path) =>
        _options.ExemptPathPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> IsAllowedAsync(IPAddress ip, ICurrentUser user, IUserRepository users, CancellationToken ct)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        IReadOnlyList<string> dbCidrs = await cache.GetOrCreateAsync("ip-allowlist:db", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await users.GetWhitelistedCidrsAsync(ct);
        }) ?? [];

        if (_options.Cidrs.Concat(dbCidrs).Any(cidr => IsInCidr(ip, cidr)))
            return true;

        // Bypass: the profile flag. Cached per user for the same window.
        bool remoteAllowed = await cache.GetOrCreateAsync($"ip-allowlist:remote:{user.UserProfileId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            UserProfileWithRole? profile = await users.GetByIdAsync(user.UserProfileId, ct);
            return profile?.RemoteAccessAllowed ?? false;
        });
        return remoteAllowed;
    }

    internal static bool IsInCidr(IPAddress ip, string cidr)
    {
        string[] parts = cidr.Split('/');
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0].Trim(), out IPAddress? baseIp)) return false;
        if (baseIp.IsIPv4MappedToIPv6) baseIp = baseIp.MapToIPv4();

        byte[] ipBytes = ip.GetAddressBytes();
        byte[] baseBytes = baseIp.GetAddressBytes();
        if (ipBytes.Length != baseBytes.Length) return false;

        int prefix = parts.Length == 2 && int.TryParse(parts[1], out int p) ? p : ipBytes.Length * 8;
        int fullBytes = prefix / 8, remainingBits = prefix % 8;

        for (int i = 0; i < fullBytes; i++)
            if (ipBytes[i] != baseBytes[i]) return false;

        if (remainingBits == 0) return true;
        int mask = (byte)~(255 >> remainingBits);
        return (ipBytes[fullBytes] & mask) == (baseBytes[fullBytes] & mask);
    }
}
