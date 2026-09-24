using Jaftim.Domain.Entities.Inquiries;

namespace Jaftim.Domain.Entities.Tagging;

/// <summary>
/// One staff member who can hold customers (EXEC CustomerTagging_GetAllManagerAgent: roles of UserTypeId 2 or 5).
/// The procedure returns UP.*; only the columns the screen needs are mapped.
/// </summary>
public sealed class TaggingAgent
{
    public long UserProfileId { get; set; }
    public string? Code { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public long? RoleId { get; set; }
    public string? RoleName { get; set; }
    public int? CountryId { get; set; }
    public string? CountryName { get; set; }
    public string? CityName { get; set; }
    public string? UserStatusName { get; set; }
    public string? Phone { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string? ImageURL { get; set; }
    public DateTime? ModifiedAt { get; set; }
    /// <summary>Customers currently tagged to this agent. NULL (not 0) when the agent has never held one.</summary>
    public int? ActiveCustomerCount { get; set; }
    /// <summary>Distinct customers ever tagged to this agent, active or not.</summary>
    public int? TotalCustomerCount { get; set; }
}

/// <summary>
/// One customer-role party as the tagging procedures return it (CustomerTagging_AgentTaggedCustomer,
/// _AgentUnTaggedCustomerHistory, _AvailableCustomers). Phone-type columns come back as 'N/A' when missing.
/// </summary>
public sealed class TaggingCustomer
{
    public long UserProfileId { get; set; }
    public string? Code { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public int? CountryId { get; set; }
    public string? CountryName { get; set; }
    public string? CityName { get; set; }
    public string? GenderName { get; set; }
    public string? UserStatusName { get; set; }
    public string? Phone { get; set; }
    public string? SecondaryPhone { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string? CustomerType { get; set; }
    public string? SourceName { get; set; }
    public string? PortOfDischarge { get; set; }
    public PartyKind? PartyKind { get; set; }
    /// <summary>Tagged/untagged lists: when the tag row last changed. Available list: when the profile last changed.</summary>
    public DateTime? ModifiedAt { get; set; }
    /// <summary>Tagged/untagged lists only: how many times this customer has been tagged to this agent.</summary>
    public int? NumberOfTimesTagged { get; set; }
    /// <summary>Untagged history only.</summary>
    public DateTime? UnTaggedDate { get; set; }
    /// <summary>Untagged history only: who removed the tag.</summary>
    public string? UnTaggedBy { get; set; }
}
