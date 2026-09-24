using Jaftim.Application.Abstractions;
using Jaftim.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Jaftim.Api.Security;

/// <summary>
/// <c>[HasPermission(Permissions.StockSave)]</c> - requires the caller's role to hold that RoleAction.ActionId in
/// RoleActionMapping. This is the server-side enforcement the legacy app had commented out; it is on by default here
/// and every non-public endpoint must carry either this attribute or a deliberate [Authorize] with a comment.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(int actionId) => Policy = $"{PolicyPrefix}{actionId}";
}

public sealed class PermissionRequirement(int actionId) : IAuthorizationRequirement
{
    public int ActionId { get; } = actionId;
}

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal)
            && int.TryParse(policyName.AsSpan(HasPermissionAttribute.PolicyPrefix.Length), out int actionId))
        {
            AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(actionId))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}

public sealed class PermissionHandler(IPermissionService permissions) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // User-based: the union of the primary role and every active, in-window UserRole row (time-bound roles).
        if (!long.TryParse(context.User.FindFirst(JaftimClaims.UserProfileId)?.Value, out long userProfileId))
            return;

        if (await permissions.HasPermissionAsync(userProfileId, requirement.ActionId))
            context.Succeed(requirement);
    }
}
