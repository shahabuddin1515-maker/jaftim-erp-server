namespace Jaftim.Domain.Entities.Inquiries;

/// <summary>
/// UserProfile.PartyKind (database/v2/005). The Customer/Contact distinction, stored instead of re-computed:
/// a CONTACT is an unqualified, lead-stage party (no real name, no valid e-mail, or no phone - typically created by
/// the lead / Respond.io pipeline with a phone-derived pseudo-email); a CUSTOMER is a qualified one.
/// Only the customer role carries it; staff profiles have none.
/// </summary>
public enum PartyKind : byte
{
    Contact = 1,
    Customer = 2,
}

/// <summary>The stored classification of one party, with how it got there.</summary>
public sealed class PartyClassification
{
    public long UserProfileId { get; set; }
    public PartyKind? PartyKind { get; set; }
    /// <summary>True when a human pinned the kind; the qualification rule then leaves it alone.</summary>
    public bool PartyKindIsManual { get; set; }
    /// <summary>First time this party became a Customer.</summary>
    public DateTime? PartyQualifiedAtUtc { get; set; }
    /// <summary>True when the call that returned this row actually changed the kind.</summary>
    public bool Changed { get; set; }
}

/// <summary>
/// GetInquiryAll row. NOTE the two layers: the Inquiry columns (FullName, Email, Phone, Message, Ad*) are a
/// SNAPSHOT taken when the enquiry arrived, while UserProfileId/IsContact/Tagged describe the party as it is NOW.
/// They legitimately differ - a lead whose e-mail was the phone digits can belong to a fully qualified customer.
/// </summary>
public sealed class InquiryListItem
{
    // --- the enquiry, as received ---
    public long InquiryId { get; set; }
    public string? Code { get; set; }
    public string? FullName { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? URL { get; set; }
    public string? Message { get; set; }
    public string? Phone { get; set; }
    public string? SecondaryPhone { get; set; }
    public string? WhatsAppNumber { get; set; }
    public string? Address { get; set; }
    public string? CountryCode { get; set; }
    public int? CountryId { get; set; }
    public string? CountryName { get; set; }
    public int? CityId { get; set; }
    public string? CityName { get; set; }
    public int? GenderId { get; set; }
    public string? GenderName { get; set; }
    public int? StatusId { get; set; }
    public string? UserStatusName { get; set; }
    public string? ImageURL { get; set; }
    public int? CustomerTypeId { get; set; }
    public string? CustomerType { get; set; }
    public int? SourceId { get; set; }
    public string? SourceName { get; set; }
    public int? PortOfDischargeId { get; set; }
    public string? PortOfDischarge { get; set; }

    // --- the party this enquiry resolved to (null until CustomerSaveInternal created one) ---
    public long? UserProfileId { get; set; }
    public string? AspNetUserId { get; set; }
    public long? RoleId { get; set; }
    public string? RoleName { get; set; }
    /// <summary>1 when the party is still unqualified - the same rule as the stored PartyKind.</summary>
    public int? IsContact { get; set; }

    // --- contact / follow-up state ---
    public bool? IsContacted { get; set; }
    public long? ContactedBy { get; set; }
    public DateTime? ContactedAt { get; set; }
    public string? Remarks { get; set; }
    public DateTime? LastRemarkUpdateDate { get; set; }
    public string? InquiryStatus { get; set; }
    public long? LeadStatusId { get; set; }

    // --- tagging (which agent owns the party) ---
    public string? Tagged { get; set; }
    public long? TaggedAgentId { get; set; }
    public DateTime? TaggedDate { get; set; }

    // --- the advert this enquiry came from ---
    public long? LeadId { get; set; }
    public long? AdId { get; set; }
    public string? Base_Make_Name { get; set; }
    public string? Base_Model_Name { get; set; }
    public int? AdYear { get; set; }
    public int? AdKms { get; set; }
    public string? AdPrice { get; set; }
    public string? AdLink { get; set; }
    public string? ReplyLink { get; set; }
    public string? AdReference { get; set; }
    public string? Old_RefId { get; set; }
    public string? Old_StockURL { get; set; }

    // --- audit ---
    public long? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public long? IsDeleted { get; set; }
    public long? CompanyId { get; set; }
    public long? RowNum { get; set; }
}

/// <summary>Inquiry_GetSectionById / Contact_GetSectionById row - a key/value pair grouped by section.</summary>
public sealed class InquirySectionValue
{
    public string? KeyName { get; set; }
    public string? KeyValue { get; set; }
    public string? SectionName { get; set; }
}

/// <summary>
/// CustomerContact row - the interaction log ("we called / WhatsApp'd / e-mailed them"). Deliberately named
/// Interaction here: it is an event, not a party, and the legacy word "contact" means both.
/// </summary>
public sealed class CustomerInteraction
{
    public long ContactId { get; set; }
    public long CustomerId { get; set; }
    /// <summary>Stored as its display name (Phone / WhatsApp / Email), not an id.</summary>
    public string? ContactType { get; set; }
    public string? ContactMessage { get; set; }
    public DateTime? ContactedTime { get; set; }
    public bool? IsActive { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
}
