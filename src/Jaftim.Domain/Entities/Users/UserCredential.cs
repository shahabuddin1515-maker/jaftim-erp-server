namespace Jaftim.Domain.Entities.Users;

/// <summary>
/// The subset of dbo.AspNetUsers the API needs to authenticate. AspNetUsers stays the credential store
/// because SQL procedures (InquiryImport_FromLead, BulkInquiryImport_V2, CustomerSaveInternal) insert
/// into it directly - replacing it would break customer creation.
/// </summary>
public sealed class UserCredential
{
    public string Id { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
}
