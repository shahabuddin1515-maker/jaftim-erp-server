using System.Security.Claims;
using Jaftim.Application.Abstractions;
using Jaftim.Infrastructure.Security;
using Jaftim.Infrastructure.Services;

namespace Jaftim.Api.Security;

/// <summary>ICurrentUser built from the validated JWT on the current request. Unauthenticated requests yield UserProfileId 0.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public long UserProfileId => Long(JaftimClaims.UserProfileId);
    public string? AccountId => Principal?.FindFirstValue(JaftimClaims.Subject) ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? Email => Principal?.FindFirstValue(JaftimClaims.Email) ?? Principal?.FindFirstValue(ClaimTypes.Email);
    public string? FullName => Principal?.FindFirstValue(JaftimClaims.FullName);
    public long RoleId => Long(JaftimClaims.RoleId);
    public int UserTypeId => (int)Long(JaftimClaims.UserTypeId);
    public int CompanyId => (int)Long(JaftimClaims.CompanyId, 1);

    private long Long(string claim, long fallback = 0) =>
        long.TryParse(Principal?.FindFirstValue(claim), out long value) ? value : fallback;
}

/// <summary>Request metadata stamped onto business audit records.</summary>
public sealed class HttpAuditRequestContext(IHttpContextAccessor accessor) : IAuditRequestContext
{
    private HttpContext? Http => accessor.HttpContext;

    public Guid? CorrelationId =>
        Guid.TryParse(Http?.TraceIdentifier, out Guid g) ? g
        : Http?.TraceIdentifier is { } id ? DeterministicGuid(id) : null;

    public string? Ip => Http?.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
        ?? Http?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString();

    // Kestrel trace ids are not GUIDs; fold them into one so the column stays a UNIQUEIDENTIFIER.
    private static Guid DeterministicGuid(string value)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
