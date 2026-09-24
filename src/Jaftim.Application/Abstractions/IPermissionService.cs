using Jaftim.Domain.Entities.Users;

namespace Jaftim.Application.Abstractions;

/// <summary>
/// Answers "may this user do X" for the current tenant. A user's permission set is the UNION of RoleActionMapping
/// over every role in effect right now: the primary role (UserProfile.RoleId) plus active, in-window UserRole rows.
/// Role -> action sets are cached 5 min; user -> effective roles 60 s, so a time-bound role starts/stops within a minute.
/// </summary>
public interface IPermissionService
{
    Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveRolesAsync(long userProfileId, CancellationToken ct = default);
    Task<IReadOnlySet<int>> GetActionIdsAsync(long userProfileId, CancellationToken ct = default);
    Task<bool> HasPermissionAsync(long userProfileId, int actionId, CancellationToken ct = default);

    /// <summary>Drop the cached action set of a role after RoleActionMapping changes.</summary>
    void InvalidateRole(long roleId);
    /// <summary>Drop the cached effective roles of a user after a UserRole / primary-role change.</summary>
    void InvalidateUser(long userProfileId);
}
