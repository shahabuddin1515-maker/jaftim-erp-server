using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Users;

/// <param name="RoleId">Existing role to rename; null creates.</param>
/// <param name="RoleName">Display name.</param>
/// <param name="UserTypeId">UserType.UserTypeId the role belongs to (legacy classification used by approval routing).</param>
/// <param name="DefaultPath">Legacy landing path; the v2 navbar derives homeRoute itself, kept for the legacy app.</param>
public sealed record RoleUpsert(long? RoleId, string RoleName, int UserTypeId, string? DefaultPath = null);

public sealed class RoleUpsertValidator : AbstractValidator<RoleUpsert>
{
    public RoleUpsertValidator()
    {
        RuleFor(x => x.RoleName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.UserTypeId).GreaterThan(0);
        RuleFor(x => x.DefaultPath).MaximumLength(200);
    }
}

/// <summary>The permission tree with, for one role, which nodes are granted.</summary>
public sealed record RolePermissionNode(int ActionId, string Name, bool Granted, IReadOnlyList<RolePermissionNode> Children);

/// <param name="ActionIds">The complete set to grant. Ancestors of every id are added automatically so the module/screen that hosts an action is always visible.</param>
public sealed record SetRolePermissionsRequest(IReadOnlyList<int> ActionIds);

/// <summary>Create or rename a permission. Ids are allocated by the database (900+).</summary>
/// <param name="ActionId">Existing id to update; null creates.</param>
/// <param name="Name">Display name, e.g. "Stock Export".</param>
/// <param name="ParentActionId">Module or screen this permission belongs to; 0/null = a new module.</param>
public sealed record PermissionUpsert(int? ActionId, string Name, int? ParentActionId);

public sealed class PermissionUpsertValidator : AbstractValidator<PermissionUpsert>
{
    public PermissionUpsertValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x).Must(x => x.ActionId is null || x.ParentActionId != x.ActionId).WithMessage("A permission cannot be its own parent.").WithName("parentActionId");
    }
}

/// <summary>Role / RoleAction / RoleActionMapping procedures (legacy RolesGetAll, RoleSave, GetRoleRightsByRoleId + v2 RoleAction_Save, RoleActionMapping_Replace).</summary>
public interface IRoleRepository
{
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default);
    Task<Role?> GetByIdAsync(long roleId, CancellationToken ct = default);
    Task SaveAsync(RoleUpsert role, CancellationToken ct = default);
    /// <summary>ActionIds currently granted to the role.</summary>
    Task<IReadOnlySet<int>> GetGrantedActionIdsAsync(long roleId, CancellationToken ct = default);
    Task<int> ReplaceGrantsAsync(long roleId, IReadOnlyCollection<int> actionIds, long actorUserProfileId, CancellationToken ct = default);
    Task<int> SavePermissionAsync(PermissionUpsert permission, long actorUserProfileId, CancellationToken ct = default);
}

public interface IRoleService
{
    Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default);
    Task<Role> UpsertAsync(RoleUpsert request, CancellationToken ct = default);
    Task<IReadOnlyList<RolePermissionNode>> GetPermissionsAsync(long roleId, CancellationToken ct = default);
    Task<IReadOnlyList<RolePermissionNode>> SetPermissionsAsync(long roleId, SetRolePermissionsRequest request, CancellationToken ct = default);
}

public sealed class RoleService(
    IRoleRepository roles,
    IUserRepository users,
    IPermissionService permissions,
    IAuditWriter audit,
    ICurrentUser actor,
    IValidator<RoleUpsert> roleValidator) : IRoleService
{
    public async Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default) =>
        (await roles.GetAllAsync(ct)).Where(r => r.IsDeleted != true).ToList();

    public async Task<Role> UpsertAsync(RoleUpsert request, CancellationToken ct = default)
    {
        await roleValidator.ValidateAndThrowAppAsync(request, ct);
        Role? before = request.RoleId is { } id ? await roles.GetByIdAsync(id, ct) ?? throw new NotFoundException("Role", id) : null;
        await roles.SaveAsync(request with { RoleName = request.RoleName.Trim() }, ct);

        Role saved = before is null
            ? (await roles.GetAllAsync(ct)).OrderByDescending(r => r.RoleId).First(r => r.RoleName == request.RoleName.Trim())
            : await roles.GetByIdAsync(before.RoleId, ct) ?? throw new NotFoundException("Role", before.RoleId);
        await audit.RecordAsync(before is null ? "Role.Created" : "Role.Updated", "Role", saved.RoleId, before, saved, ct);
        return saved;
    }

    public async Task<IReadOnlyList<RolePermissionNode>> GetPermissionsAsync(long roleId, CancellationToken ct = default)
    {
        _ = await roles.GetByIdAsync(roleId, ct) ?? throw new NotFoundException("Role", roleId);
        IReadOnlySet<int> granted = await roles.GetGrantedActionIdsAsync(roleId, ct);
        return BuildTree(await users.GetAllRoleActionsAsync(ct), granted);
    }

    public async Task<IReadOnlyList<RolePermissionNode>> SetPermissionsAsync(long roleId, SetRolePermissionsRequest request, CancellationToken ct = default)
    {
        _ = await roles.GetByIdAsync(roleId, ct) ?? throw new NotFoundException("Role", roleId);
        IReadOnlyList<RoleAction> catalog = await users.GetAllRoleActionsAsync(ct);
        Dictionary<int, RoleAction> byId = catalog.ToDictionary(a => a.ActionId);

        int[] unknown = request.ActionIds.Where(id => !byId.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
            throw new BusinessRuleException($"Unknown permission id(s): {string.Join(", ", unknown)}.");

        IReadOnlySet<int> closed = WithAncestors(request.ActionIds, byId);
        IReadOnlySet<int> before = await roles.GetGrantedActionIdsAsync(roleId, ct);
        await roles.ReplaceGrantsAsync(roleId, closed, actor.UserProfileId, ct);

        permissions.InvalidateRole(roleId);
        await audit.RecordAsync("Role.PermissionsChanged", "Role", roleId,
            new { Granted = before.Order() }, new { Granted = closed.Order(), Added = closed.Except(before).Order(), Removed = before.Except(closed).Order() }, ct);
        return BuildTree(catalog, closed);
    }

    /// <summary>Granting a screen or action implies its module/screen, otherwise the navbar could never reach it.</summary>
    internal static IReadOnlySet<int> WithAncestors(IEnumerable<int> ids, IReadOnlyDictionary<int, RoleAction> byId)
    {
        var set = new HashSet<int>();
        foreach (int id in ids)
        {
            int? current = id;
            int guard = 0;
            while (current is { } c && byId.TryGetValue(c, out RoleAction? action) && set.Add(c) && guard++ < 20)
                current = action.ActionParentId == 0 ? null : action.ActionParentId;
        }
        return set;
    }

    private static IReadOnlyList<RolePermissionNode> BuildTree(IReadOnlyList<RoleAction> catalog, IReadOnlySet<int> granted)
    {
        ILookup<int, RoleAction> byParent = catalog.OrderBy(a => a.ActionId).ToLookup(a => a.ActionParentId);
        return Build(0, byParent, granted, 0);
    }

    private static IReadOnlyList<RolePermissionNode> Build(int parentId, ILookup<int, RoleAction> byParent, IReadOnlySet<int> granted, int depth) =>
        depth > 10 ? [] : byParent[parentId]
            .Select(a => new RolePermissionNode(a.ActionId, a.ActionName, granted.Contains(a.ActionId), Build(a.ActionId, byParent, granted, depth + 1)))
            .ToList();
}
