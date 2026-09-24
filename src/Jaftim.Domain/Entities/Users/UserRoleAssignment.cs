namespace Jaftim.Domain.Entities.Users;

/// <summary>tenant dbo.UserRole row (UserRole_GetByUser). IsPrimary is computed against UserProfile.RoleId.</summary>
public sealed class UserRoleAssignment
{
    public long UserRoleId { get; set; }
    public long UserProfileId { get; set; }
    public long RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public DateTime? ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }
    public string? Reason { get; set; }
    public bool IsActive { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public long? ModifiedBy { get; set; }
}

/// <summary>UserRole_GetEffective row - a role in effect right now.</summary>
public sealed class EffectiveUserRole
{
    public long RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DateTime? ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }
}
