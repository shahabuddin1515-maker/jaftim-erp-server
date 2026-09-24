namespace Jaftim.Domain.Entities.Navigation;

/// <summary>tenant dbo.NavigationItem row (database/v2/003_Navigation.sql). ParentId NULL = module.</summary>
public sealed class NavigationItem
{
    public int NavigationItemId { get; set; }
    public int? ParentId { get; set; }
    /// <summary>Stable key the frontend maps to a route component, e.g. "stock.list".</summary>
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Icon { get; set; }
    /// <summary>Frontend path; null for a pure group.</summary>
    public string? Route { get; set; }
    public int SortOrder { get; set; }
    /// <summary>RoleAction.ActionId that gates the item; null = any signed-in user (subject to RequiredRoleIdsCsv).</summary>
    public int? ActionId { get; set; }
    /// <summary>Comma-separated Role.RoleId list; when set, one of the caller's effective roles must be in it.</summary>
    public string? RequiredRoleIdsCsv { get; set; }
    public bool IsActive { get; set; }
}
