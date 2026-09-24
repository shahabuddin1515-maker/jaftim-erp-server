using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Users;
using Microsoft.Extensions.Caching.Memory;

namespace Jaftim.Infrastructure.Security;

/// <summary>
/// Permissions = union of RoleActionMapping over the user's effective roles (primary + active in-window UserRole
/// rows). Two cache layers, both keyed by tenant: role -> action ids (5 min; invalidated when role rights are
/// saved) and user -> effective roles (60 s; invalidated when the user's roles change). A time-bound role therefore
/// takes effect / lapses within a minute without any background job.
/// </summary>
public sealed class CachedPermissionService(
    IUserRepository users,
    IUserRoleRepository userRoles,
    ITenantContext tenant,
    IDateTimeProvider clock,
    IMemoryCache cache) : IPermissionService
{
    private static readonly TimeSpan RoleCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UserCacheDuration = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveRolesAsync(long userProfileId, CancellationToken ct = default)
    {
        IReadOnlyList<EffectiveUserRole>? roles = await cache.GetOrCreateAsync(UserKey(userProfileId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = UserCacheDuration;
            return await userRoles.GetEffectiveAsync(userProfileId, clock.UtcNow, ct);
        });
        return roles ?? [];
    }

    public async Task<IReadOnlySet<int>> GetActionIdsAsync(long userProfileId, CancellationToken ct = default)
    {
        var union = new HashSet<int>();
        foreach (EffectiveUserRole role in await GetEffectiveRolesAsync(userProfileId, ct))
            union.UnionWith(await GetRoleActionIdsAsync(role.RoleId, ct));
        return union;
    }

    public async Task<bool> HasPermissionAsync(long userProfileId, int actionId, CancellationToken ct = default)
    {
        foreach (EffectiveUserRole role in await GetEffectiveRolesAsync(userProfileId, ct))
            if ((await GetRoleActionIdsAsync(role.RoleId, ct)).Contains(actionId))
                return true;
        return false;
    }

    public void InvalidateRole(long roleId) => cache.Remove(RoleKey(roleId));

    public void InvalidateUser(long userProfileId) => cache.Remove(UserKey(userProfileId));

    private async Task<IReadOnlySet<int>> GetRoleActionIdsAsync(long roleId, CancellationToken ct)
    {
        IReadOnlySet<int>? set = await cache.GetOrCreateAsync(RoleKey(roleId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = RoleCacheDuration;
            IReadOnlyList<RoleAction> actions = await users.GetRoleActionsAsync(roleId, ct);
            return (IReadOnlySet<int>)actions.Select(a => a.ActionId).ToHashSet();
        });
        return set ?? new HashSet<int>();
    }

    private string RoleKey(long roleId) => $"perm:t{tenant.TenantId}:role:{roleId}";
    private string UserKey(long userProfileId) => $"perm:t{tenant.TenantId}:user:{userProfileId}";
}
