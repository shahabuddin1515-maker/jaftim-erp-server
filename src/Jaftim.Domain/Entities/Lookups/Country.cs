namespace Jaftim.Domain.Entities.Lookups;

/// <summary>
/// A live, active Base_Country (Lookup_Countries, database/v2/017). Id is what UserProfile.CountryId stores and what
/// GET /api/lookups/cities takes; DialCodeId is what the user endpoints take as phoneCountryCodeId /
/// whatsAppCountryCodeId.
/// </summary>
public sealed class Country
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>ISO 3166-1 alpha-2 code - for flags in the UI.</summary>
    public string? Alpha2Code { get; set; }
    /// <summary>Base_CountryCodes id of the country's dial code; null when it has none.</summary>
    public long? DialCodeId { get; set; }
    /// <summary>The dial code itself, e.g. "+92".</summary>
    public string? DialCode { get; set; }
}
