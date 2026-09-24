using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;
using Jaftim.Domain.Security;

namespace Jaftim.Application.Modules.Users;

/// <summary>Assign (or re-scope) a role. Null dates = immediately / permanent.</summary>
/// <param name="RoleId">Role.RoleId (tenant Role table). The Customer role (3) is refused for staff.</param>
/// <param name="ValidFromUtc">When the role starts applying; null = now.</param>
/// <param name="ValidToUtc">When the role stops applying (exclusive); null = permanent. Must be after ValidFromUtc.</param>
/// <param name="Reason">Free text shown in the audit trail (max 400).</param>
public sealed record AssignRoleRequest(long RoleId, DateTime? ValidFromUtc = null, DateTime? ValidToUtc = null, string? Reason = null);

/// <param name="RoleId">The new primary role - what the legacy stored procedures see for scoping and routing.</param>
public sealed record SetPrimaryRoleRequest(long RoleId);

public sealed class AssignRoleRequestValidator : AbstractValidator<AssignRoleRequest>
{
    public AssignRoleRequestValidator()
    {
        RuleFor(x => x.RoleId).GreaterThan(0);
        RuleFor(x => x.Reason).MaximumLength(400);
        RuleFor(x => x)
            .Must(x => x.ValidFromUtc is null || x.ValidToUtc is null || x.ValidToUtc > x.ValidFromUtc)
            .WithMessage("validToUtc must be after validFromUtc.")
            .WithName("validToUtc");
    }
}

/// <summary>Tenant UserRole_* procedures.</summary>
public interface IUserRoleRepository
{
    Task<IReadOnlyList<UserRoleAssignment>> GetByUserAsync(long userProfileId, CancellationToken ct = default);
    Task<IReadOnlyList<EffectiveUserRole>> GetEffectiveAsync(long userProfileId, DateTime asOfUtc, CancellationToken ct = default);
    Task<long> SaveAsync(long userProfileId, long roleId, DateTime? validFromUtc, DateTime? validToUtc, string? reason, long actorUserProfileId, CancellationToken ct = default);
    Task<int> RevokeAsync(long userProfileId, long roleId, long actorUserProfileId, CancellationToken ct = default);
    Task SetPrimaryAsync(long userProfileId, long roleId, long actorUserProfileId, CancellationToken ct = default);
}

public interface IUserRoleService
{
    Task<IReadOnlyList<UserRoleAssignment>> ListAsync(long userProfileId, CancellationToken ct = default);
    Task<IReadOnlyList<UserRoleAssignment>> AssignAsync(long userProfileId, AssignRoleRequest request, CancellationToken ct = default);
    Task RevokeAsync(long userProfileId, long roleId, CancellationToken ct = default);
    Task SetPrimaryAsync(long userProfileId, SetPrimaryRoleRequest request, CancellationToken ct = default);
}

/// <summary>
/// Rules: the user and role must exist in this tenant; staff cannot be given the Customer role; the primary role
/// cannot be revoked (change it first); every change invalidates the permission cache and is audited.
/// </summary>
public sealed class UserRoleService(
    IUserRoleRepository userRoles,
    IUserRepository users,
    IPermissionService permissions,
    IAuditWriter audit,
    ICurrentUser actor,
    IValidator<AssignRoleRequest> assignValidator) : IUserRoleService
{
    public async Task<IReadOnlyList<UserRoleAssignment>> ListAsync(long userProfileId, CancellationToken ct = default)
    {
        await EnsureUserAsync(userProfileId, ct);
        return await userRoles.GetByUserAsync(userProfileId, ct);
    }

    public async Task<IReadOnlyList<UserRoleAssignment>> AssignAsync(long userProfileId, AssignRoleRequest request, CancellationToken ct = default)
    {
        await assignValidator.ValidateAndThrowAppAsync(request, ct);
        UserProfileWithRole user = await EnsureUserAsync(userProfileId, ct);
        Role role = await EnsureRoleAsync(request.RoleId, ct);

        if (user.RoleId != RoleIds.Customer && role.RoleId == RoleIds.Customer)
            throw new BusinessRuleException("The Customer role cannot be assigned to a staff profile.");
        if (user.RoleId == RoleIds.Customer && role.RoleId != RoleIds.Customer)
            throw new BusinessRuleException("A customer profile cannot be given a staff role.");
        if (role.RoleId == user.RoleId && (request.ValidFromUtc is not null || request.ValidToUtc is not null))
            throw new BusinessRuleException("The primary role is permanent; change the primary role instead of time-boxing it.");

        IReadOnlyList<UserRoleAssignment> before = await userRoles.GetByUserAsync(userProfileId, ct);
        await userRoles.SaveAsync(userProfileId, request.RoleId, request.ValidFromUtc, request.ValidToUtc, request.Reason, actor.UserProfileId, ct);
        IReadOnlyList<UserRoleAssignment> after = await userRoles.GetByUserAsync(userProfileId, ct);

        permissions.InvalidateUser(userProfileId);
        await audit.RecordAsync("UserRole.Assigned", "UserProfile", userProfileId,
            before.FirstOrDefault(r => r.RoleId == request.RoleId), after.First(r => r.RoleId == request.RoleId), ct);
        return after;
    }

    public async Task RevokeAsync(long userProfileId, long roleId, CancellationToken ct = default)
    {
        UserProfileWithRole user = await EnsureUserAsync(userProfileId, ct);
        if (user.RoleId == roleId)
            throw new BusinessRuleException("The primary role cannot be revoked; set a different primary role first.");

        IReadOnlyList<UserRoleAssignment> before = await userRoles.GetByUserAsync(userProfileId, ct);
        UserRoleAssignment existing = before.FirstOrDefault(r => r.RoleId == roleId)
            ?? throw new NotFoundException("User role", roleId);

        await userRoles.RevokeAsync(userProfileId, roleId, actor.UserProfileId, ct);
        permissions.InvalidateUser(userProfileId);
        await audit.RecordAsync("UserRole.Revoked", "UserProfile", userProfileId, existing, null, ct);
    }

    public async Task SetPrimaryAsync(long userProfileId, SetPrimaryRoleRequest request, CancellationToken ct = default)
    {
        UserProfileWithRole user = await EnsureUserAsync(userProfileId, ct);
        Role role = await EnsureRoleAsync(request.RoleId, ct);
        if ((user.RoleId == RoleIds.Customer) != (role.RoleId == RoleIds.Customer))
            throw new BusinessRuleException("A profile cannot change between customer and staff through its primary role.");
        if (user.RoleId == role.RoleId) return;

        await userRoles.SetPrimaryAsync(userProfileId, role.RoleId, actor.UserProfileId, ct);
        permissions.InvalidateUser(userProfileId);
        await audit.RecordAsync("UserProfile.PrimaryRoleChanged", "UserProfile", userProfileId,
            new { RoleId = user.RoleId, user.RoleName }, new { role.RoleId, role.RoleName }, ct);
    }

    private async Task<UserProfileWithRole> EnsureUserAsync(long userProfileId, CancellationToken ct) =>
        await users.GetByIdAsync(userProfileId, ct) is { } u && (u.IsDeleted ?? 0) == 0
            ? u
            : throw new NotFoundException("User", userProfileId);

    private async Task<Role> EnsureRoleAsync(long roleId, CancellationToken ct) =>
        (await users.GetRolesAsync(ct)).FirstOrDefault(r => r.RoleId == roleId && r.IsDeleted != true)
            ?? throw new NotFoundException("Role", roleId);
}
