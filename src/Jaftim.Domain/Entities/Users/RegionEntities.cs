namespace Jaftim.Domain.Entities.Users;

/// <summary>Entity.EntityTypeId values (legacy EntityType seed: Division = 1, Group = 2). Live-verified 2026-10-05.</summary>
public static class RegionEntityTypes
{
    public const int Division = 1;
    public const int Group = 2;
}

/// <summary>A Division or Group row of the legacy Entity table (UserRegion_Get result set 1).</summary>
public sealed class RegionEntityRow
{
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public int EntityTypeId { get; set; }
    public long? ParentEntityId { get; set; }
}

/// <summary>A Base_Country row as the region tree sees it (UserRegion_Get result set 2).</summary>
public sealed class RegionCountryRow
{
    public long CountryId { get; set; }
    public string CountryName { get; set; } = string.Empty;
    /// <summary>fn_GetUserAccessibleCountries ignores inactive countries, so granting one gives no access.</summary>
    public bool IsActive { get; set; }
    /// <summary>Base_Country.EntityId - the Group the country belongs to; null for unplaced (e.g. test) countries.</summary>
    public long? GroupEntityId { get; set; }
}
