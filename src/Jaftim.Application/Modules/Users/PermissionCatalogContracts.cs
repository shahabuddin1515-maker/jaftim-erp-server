using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Users;

/// <summary>A RoleAction and its children (ActionParentId). Roots (ActionParentId = 0) are the modules.</summary>
public sealed record PermissionNode(
    int ActionId,
    string Name,
    string CssClass,
    string? LegacyRoute,
    IReadOnlyList<PermissionNode> Children);

public interface IPermissionCatalogService
{
    /// <summary>The whole RoleAction hierarchy - drives the navbar, the role-rights editor and per-button hiding in the UI.</summary>
    Task<IReadOnlyList<PermissionNode>> GetCatalogAsync(CancellationToken ct = default);
    /// <summary>Create or rename/re-parent a permission. New ids (900+) are allocated by the database.</summary>
    Task<PermissionNode> UpsertAsync(PermissionUpsert request, CancellationToken ct = default);
}

public sealed class PermissionCatalogService(
    IUserRepository users,
    IRoleRepository roles,
    IPermissionService permissions,
    IAuditWriter audit,
    ICurrentUser actor,
    IValidator<PermissionUpsert> validator) : IPermissionCatalogService
{
    public async Task<IReadOnlyList<PermissionNode>> GetCatalogAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RoleAction> all = await users.GetAllRoleActionsAsync(ct);
        ILookup<int, RoleAction> byParent = all.OrderBy(a => a.ActionId).ToLookup(a => a.ActionParentId);
        return Build(0, byParent, depth: 0);
    }

    public async Task<PermissionNode> UpsertAsync(PermissionUpsert request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        IReadOnlyList<RoleAction> all = await users.GetAllRoleActionsAsync(ct);
        Dictionary<int, RoleAction> byId = all.ToDictionary(a => a.ActionId);

        RoleAction? existing = request.ActionId is { } id ? byId.GetValueOrDefault(id) ?? throw new NotFoundException("Permission", id) : null;
        int parentId = request.ParentActionId ?? 0;
        if (parentId != 0)
        {
            if (!byId.ContainsKey(parentId)) throw new NotFoundException("Parent permission", parentId);
            if (existing is not null && IsDescendant(byId, parentId, existing.ActionId))
                throw new BusinessRuleException("A permission cannot be moved under one of its own descendants.");
        }

        int savedId = await roles.SavePermissionAsync(request with { Name = request.Name.Trim(), ParentActionId = parentId }, actor.UserProfileId, ct);
        RoleAction saved = (await users.GetAllRoleActionsAsync(ct)).First(a => a.ActionId == savedId);

        // A re-parent changes ancestor closure for every role that holds it; drop the role caches so it applies now.
        if (existing is not null && existing.ActionParentId != parentId)
            foreach (Role role in await roles.GetAllAsync(ct)) permissions.InvalidateRole(role.RoleId);

        await audit.RecordAsync(existing is null ? "Permission.Created" : "Permission.Updated", "RoleAction", savedId, existing, saved, ct);
        return new PermissionNode(saved.ActionId, saved.ActionName, saved.ActionCssClass, null, []);
    }

    private static bool IsDescendant(IReadOnlyDictionary<int, RoleAction> byId, int candidateId, int ancestorId)
    {
        int current = candidateId, guard = 0;
        while (current != 0 && guard++ < 20)
        {
            if (current == ancestorId) return true;
            current = byId.TryGetValue(current, out RoleAction? a) ? a.ActionParentId : 0;
        }
        return false;
    }

    private static IReadOnlyList<PermissionNode> Build(int parentId, ILookup<int, RoleAction> byParent, int depth) =>
        depth > 10 ? [] : byParent[parentId]
            .Select(a => new PermissionNode(a.ActionId, a.ActionName, a.ActionCssClass,
                string.IsNullOrWhiteSpace(a.ActionMvcPermission) || a.ActionMvcPermission == "/" ? null : a.ActionMvcPermission,
                Build(a.ActionId, byParent, depth + 1)))
            .ToList();
}
