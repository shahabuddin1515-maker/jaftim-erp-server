namespace Jaftim.Domain.Entities.Lookups;

/// <summary>A Base_City row (GET /api/lookups/cities?countryId=). Its Id is what UserProfile.CityId stores.</summary>
public sealed class City
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
