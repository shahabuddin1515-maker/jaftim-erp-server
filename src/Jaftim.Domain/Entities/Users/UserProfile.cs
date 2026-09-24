namespace Jaftim.Domain.Entities.Users;

/// <summary>
/// dbo.UserProfile. Staff AND customers live here, distinguished by RoleId (customer = RoleIds.Customer).
/// Column names mirror the table so Dapper maps by convention.
/// </summary>
public sealed class UserProfile : AuditedEntity
{
    public long UserProfileId { get; set; }
    public string? Code { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? URL { get; set; }
    /// <summary>AspNetUsers.Id - the credential record. Nullable for legacy rows.</summary>
    public string? AspNetUserId { get; set; }
    public long RoleId { get; set; }
    public int GenderId { get; set; }
    public string? Address { get; set; }
    public string? CountryCode { get; set; }
    public int? CountryId { get; set; }
    public int? CityId { get; set; }
    public int StatusId { get; set; }
    public string? ImageURL { get; set; }
    public long? ReportsTo_UserId { get; set; }
    public bool? RemoteAccessAllowed { get; set; }
    public long? RI_ContactId { get; set; }
}
