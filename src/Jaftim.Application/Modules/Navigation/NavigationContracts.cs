using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Users;
using Jaftim.Domain.Entities.Navigation;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Navigation;

/// <summary>One node of the navbar the frontend renders. Modules are the roots; leaves carry a route.</summary>
public sealed record NavigationNode(
    string Code,
    string Title,
    string? Icon,
    string? Route,
    int? ActionId,
    IReadOnlyList<NavigationNode> Children);

/// <summary>What GET /api/navigation/me returns.</summary>
public sealed record NavigationResponse(IReadOnlyList<NavigationNode> Modules, string? HomeRoute);

/// <summary>Admin upsert of one navbar item.</summary>
/// <param name="NavigationItemId">Existing id to update; null creates.</param>
/// <param name="ParentCode">Code of the parent item; null makes this a top-level module.</param>
/// <param name="Code">Stable key, unique, e.g. "stock.list". The frontend maps it to a route component.</param>
/// <param name="Title">Label shown in the navbar.</param>
/// <param name="Icon">Icon name the frontend resolves (Font Awesome class today).</param>
/// <param name="Route">Frontend path (must start with /); null for a pure group.</param>
/// <param name="SortOrder">Position among siblings (ascending).</param>
/// <param name="ActionId">RoleAction.ActionId that must be held to see the item (required unless NewPermissionName is given).</param>
/// <param name="RequiredRoleIds">If non-empty, one of the caller's effective roles must be listed.</param>
/// <param name="IsActive">Inactive items are kept for the admin screen but never rendered.</param>
/// <param name="NewPermissionName">Alternative to ActionId: create a permission with this name under the parent node permission (a new module when ParentCode is null) and gate the item with it.</param>
public sealed record NavigationItemUpsert(
    int? NavigationItemId,
    string? ParentCode,
    string Code,
    string Title,
    string? Icon,
    string? Route,
    int SortOrder,
    int? ActionId,
    IReadOnlyList<long>? RequiredRoleIds,
    bool IsActive = true,
    string? NewPermissionName = null);

public sealed class NavigationItemUpsertValidator : AbstractValidator<NavigationItemUpsert>
{
    public NavigationItemUpsertValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(100).Matches("^[a-z0-9]+(?:[.-][a-z0-9]+)*$").WithMessage("Use lower-case dotted codes such as stock.list.");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Icon).MaximumLength(100);
        RuleFor(x => x.Route).MaximumLength(200).Must(r => r is null || r.StartsWith('/')).WithMessage("Route must start with /.");
        RuleFor(x => x.ParentCode).MaximumLength(100);
        RuleFor(x => x).Must(x => !string.Equals(x.Code, x.ParentCode, StringComparison.OrdinalIgnoreCase)).WithMessage("An item cannot be its own parent.").WithName("parentCode");
        RuleFor(x => x).Must(x => x.ActionId is not null ^ !string.IsNullOrWhiteSpace(x.NewPermissionName))
            .WithMessage("Every navigation item is gated by a permission: give either actionId or newPermissionName (not both).").WithName("actionId");
        RuleFor(x => x.NewPermissionName).MaximumLength(200);
    }
}

/// <summary>Navigation_* procedures.</summary>
public interface INavigationRepository
{
    Task<IReadOnlyList<NavigationItem>> GetAllAsync(CancellationToken ct = default);
    Task<int> SaveAsync(NavigationItem item, long actorUserProfileId, CancellationToken ct = default);
    Task<int> DeleteAsync(int navigationItemId, long actorUserProfileId, CancellationToken ct = default);
}

public interface INavigationService
{
    /// <summary>The tree filtered for the current user: a node is kept when its own gate passes or any child is kept.</summary>
    Task<NavigationResponse> GetForCurrentUserAsync(CancellationToken ct = default);
    /// <summary>The full tree (inactive items included) for the admin screen.</summary>
    Task<IReadOnlyList<NavigationItem>> GetAllAsync(CancellationToken ct = default);
    Task<NavigationItem> UpsertAsync(NavigationItemUpsert request, CancellationToken ct = default);
    Task DeleteAsync(int navigationItemId, CancellationToken ct = default);
}

public sealed class NavigationService(
    INavigationRepository repository,
    IPermissionCatalogService catalog,
    IPermissionService permissions,
    ICurrentUser user,
    IAuditWriter audit,
    IValidator<NavigationItemUpsert> validator) : INavigationService
{
    public async Task<NavigationResponse> GetForCurrentUserAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NavigationItem> items = await repository.GetAllAsync(ct);
        IReadOnlySet<int> actions = await permissions.GetActionIdsAsync(user.UserProfileId, ct);
        HashSet<long> roles = (await permissions.GetEffectiveRolesAsync(user.UserProfileId, ct)).Select(r => r.RoleId).ToHashSet();

        ILookup<int?, NavigationItem> byParent = items.Where(i => i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Title).ToLookup(i => i.ParentId);
        IReadOnlyList<NavigationNode> modules = Build(null, byParent, actions, roles);
        return new NavigationResponse(modules, FirstRoute(modules));
    }

    public Task<IReadOnlyList<NavigationItem>> GetAllAsync(CancellationToken ct = default) => repository.GetAllAsync(ct);

    public async Task<NavigationItem> UpsertAsync(NavigationItemUpsert request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        IReadOnlyList<NavigationItem> all = await repository.GetAllAsync(ct);

        NavigationItem? existing = request.NavigationItemId is { } id ? all.FirstOrDefault(i => i.NavigationItemId == id) : null;
        if (request.NavigationItemId is not null && existing is null)
            throw new NotFoundException("Navigation item", request.NavigationItemId);
        if (all.Any(i => i.Code.Equals(request.Code, StringComparison.OrdinalIgnoreCase) && i.NavigationItemId != existing?.NavigationItemId))
            throw new ConflictException($"Code '{request.Code}' is already used.");

        int? parentId = null;
        if (!string.IsNullOrWhiteSpace(request.ParentCode))
        {
            NavigationItem parent = all.FirstOrDefault(i => i.Code.Equals(request.ParentCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotFoundException("Parent navigation item", request.ParentCode);
            if (existing is not null && IsDescendant(all, parent.NavigationItemId, existing.NavigationItemId))
                throw new BusinessRuleException("An item cannot be moved under one of its own descendants.");
            parentId = parent.NavigationItemId;
        }

        int actionId = request.ActionId
            ?? (await catalog.UpsertAsync(new PermissionUpsert(null, request.NewPermissionName!,
                    parentId is { } pid ? all.First(i => i.NavigationItemId == pid).ActionId : null), ct)).ActionId;

        var item = new NavigationItem
        {
            NavigationItemId = existing?.NavigationItemId ?? 0,
            ParentId = parentId,
            Code = request.Code,
            Title = request.Title.Trim(),
            Icon = request.Icon,
            Route = request.Route,
            SortOrder = request.SortOrder,
            ActionId = actionId,
            RequiredRoleIdsCsv = request.RequiredRoleIds is { Count: > 0 } r ? string.Join(",", r.Distinct()) : null,
            IsActive = request.IsActive,
        };
        item.NavigationItemId = await repository.SaveAsync(item, user.UserProfileId, ct);
        await audit.RecordAsync(existing is null ? "Navigation.Created" : "Navigation.Updated", "NavigationItem", item.NavigationItemId, existing, item, ct);
        return item;
    }

    public async Task DeleteAsync(int navigationItemId, CancellationToken ct = default)
    {
        NavigationItem existing = (await repository.GetAllAsync(ct)).FirstOrDefault(i => i.NavigationItemId == navigationItemId)
            ?? throw new NotFoundException("Navigation item", navigationItemId);
        await repository.DeleteAsync(navigationItemId, user.UserProfileId, ct);
        await audit.RecordAsync("Navigation.Deleted", "NavigationItem", navigationItemId, existing, null, ct);
    }

    // ---------- tree helpers ----------

    private static IReadOnlyList<NavigationNode> Build(int? parentId, ILookup<int?, NavigationItem> byParent, IReadOnlySet<int> actions, HashSet<long> roles)
    {
        var nodes = new List<NavigationNode>();
        foreach (NavigationItem item in byParent[parentId])
        {
            bool selfVisible = GatePasses(item, actions, roles);
            IReadOnlyList<NavigationNode> children = selfVisible || item.Route is null ? Build(item.NavigationItemId, byParent, actions, roles) : [];

            // A group (no route) is shown only when something under it is visible; a link needs its own gate to pass.
            bool keep = item.Route is null ? children.Count > 0 && selfVisible : selfVisible;
            if (keep)
                nodes.Add(new NavigationNode(item.Code, item.Title, item.Icon, item.Route, item.ActionId, children));
        }
        return nodes;
    }

    /// <summary>Every node is gated by its permission (a node without one is never shown); RequiredRoleIdsCsv is an optional extra restriction.</summary>
    internal static bool GatePasses(NavigationItem item, IReadOnlySet<int> actions, IReadOnlySet<long> roles)
    {
        if (item.ActionId is not { } actionId || !actions.Contains(actionId)) return false;
        if (string.IsNullOrWhiteSpace(item.RequiredRoleIdsCsv)) return true;
        return item.RequiredRoleIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => long.TryParse(s, out long r) && roles.Contains(r));
    }

    private static string? FirstRoute(IReadOnlyList<NavigationNode> nodes)
    {
        foreach (NavigationNode n in nodes)
        {
            if (n.Route is not null) return n.Route;
            if (FirstRoute(n.Children) is { } r) return r;
        }
        return null;
    }

    private static bool IsDescendant(IReadOnlyList<NavigationItem> all, int candidateId, int ancestorId)
    {
        int? current = candidateId;
        while (current is not null)
        {
            if (current == ancestorId) return true;
            current = all.FirstOrDefault(i => i.NavigationItemId == current)?.ParentId;
        }
        return false;
    }
}
