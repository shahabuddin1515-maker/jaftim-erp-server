using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Auth;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Enums;
using Jaftim.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;

namespace Jaftim.Api.Security;

/// <summary>
/// Runs after signature/expiry validation on every request. Binds the scoped tenant context from the "tid" claim,
/// then rejects the token when the catalog Account.TokenVersion has moved on (logout-all, password change, replay)
/// or the tenant profile is no longer active - the legacy BaseController re-checked active status on every request;
/// this keeps that guarantee with a short cache.
/// </summary>
public sealed class TokenVersionValidator(IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public async Task ValidateAsync(TokenValidatedContext context)
    {
        string? accountId = context.Principal?.FindFirst(JaftimClaims.Subject)?.Value;
        string? versionClaim = context.Principal?.FindFirst(JaftimClaims.TokenVersion)?.Value;
        string? tenantClaim = context.Principal?.FindFirst(JaftimClaims.TenantId)?.Value;
        string tenantCode = context.Principal?.FindFirst(JaftimClaims.TenantCode)?.Value ?? string.Empty;
        string? userClaim = context.Principal?.FindFirst(JaftimClaims.UserProfileId)?.Value;

        if (accountId is null || !int.TryParse(versionClaim, out int tokenVersion)
            || !int.TryParse(tenantClaim, out int tenantId) || !long.TryParse(userClaim, out long userProfileId))
        {
            context.Fail("Token is missing required claims.");
            return;
        }

        IServiceProvider services = context.HttpContext.RequestServices;
        services.GetRequiredService<ITenantContextSetter>().Set(tenantId, tenantCode);

        AuthState state = await cache.GetOrCreateAsync($"authstate:{accountId}:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            CancellationToken ct = context.HttpContext.RequestAborted;
            Account? account = await services.GetRequiredService<IAuthRepository>().GetAccountByIdAsync(accountId, ct);
            UserProfileWithRole? profile = await services.GetRequiredService<IUserRepository>().GetByIdAsync(userProfileId, ct);
            // The uid/tid pair comes from a signed token issued against AccountTenant, so no membership re-check here.
            bool active = account is { IsActive: true }
                && profile is { } p && p.StatusId == (int)UserStatus.Active && (p.IsDeleted ?? 0) == 0;
            return new AuthState(account?.TokenVersion ?? int.MinValue, active);
        }) ?? new AuthState(int.MinValue, false);

        if (state.TokenVersion != tokenVersion)
            context.Fail("Token has been revoked.");
        else if (!state.IsActive)
            context.Fail("Your account is currently inactive.");
    }

    /// <summary>Call after any change that must take effect before the cache window ends.</summary>
    public void Invalidate(string accountId, int tenantId) => cache.Remove($"authstate:{accountId}:{tenantId}");

    private sealed record AuthState(int TokenVersion, bool IsActive);
}
