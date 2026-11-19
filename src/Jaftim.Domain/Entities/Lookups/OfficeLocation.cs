namespace Jaftim.Domain.Entities.Lookups;

/// <summary>
/// An office-location country: a Base_Country row with IsLocationCountry = 1 (Lookup_OfficeLocations,
/// database/v2/013). Its Id is a Base_Country id - NOT a value for UserProfileDetail.OfficeLocation_CountryId, which
/// holds Base_OriginCountry ids (docs/USERS.md "Office location").
/// </summary>
public sealed class OfficeLocation
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>ISO 3166-1 alpha-2 code (AU, JP, AE, GB) - for flags in the UI.</summary>
    public string? Alpha2Code { get; set; }
}
