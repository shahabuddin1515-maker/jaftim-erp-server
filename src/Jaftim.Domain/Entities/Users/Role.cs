namespace Jaftim.Domain.Entities.Users;

/// <summary>dbo.Role.</summary>
public sealed class Role
{
    public long RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string? DefaultPath { get; set; }
    public int UserTypeId { get; set; }
    public bool? IsDeleted { get; set; }
    /// <summary>List reads only (RolesGetAll / RolesGetAll_V2 join UserType); null from RoleGetById.</summary>
    public string? UserTypeName { get; set; }
    public DateTime? CreatedAt { get; set; }
    /// <summary>ISNULL(ModifiedAt, CreatedAt), as the list procedures return it.</summary>
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>dbo.RoleAction - the permission catalog row (see Jaftim.Domain.Security.Permissions).</summary>
public sealed class RoleAction
{
    public int ActionId { get; set; }
    public string ActionName { get; set; } = string.Empty;
    public string ActionCssClass { get; set; } = string.Empty;
    public string? ActionMvcPermission { get; set; }
    public int ActionParentId { get; set; }
    public int ActionIdentification { get; set; }
    public int? DependantId { get; set; }
}
