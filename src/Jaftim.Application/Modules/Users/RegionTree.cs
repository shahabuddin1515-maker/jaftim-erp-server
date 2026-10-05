using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Users;

/// <summary>
/// The Division -> Group -> Country tree of one <see cref="UserRegionSnapshot"/>, shared by user-region assignment and
/// region administration. The database enforces none of the shape (docs/USERS.md defect 2), so it is enforced here:
/// a division has no parent, a group sits under a live division, a country is "placed" only under such a group.
/// Rows that break the shape are kept aside (orphan groups, unplaced countries), never silently attached.
/// </summary>
internal sealed class RegionTree
{
    private readonly Dictionary<long, RegionEntityRow> _divisions;
    private readonly Dictionary<long, RegionEntityRow> _groups;          // only groups under a valid division
    private readonly Dictionary<long, RegionEntityRow> _orphanGroups;    // groups with no or a missing division
    private readonly Dictionary<long, RegionCountryRow> _countries;      // every non-deleted country
    private readonly ILookup<long, RegionCountryRow> _countriesByGroup;  // keyed by GroupEntityId, any group
    private readonly HashSet<long> _ticked;
    private readonly HashSet<long> _granted;

    private RegionTree(UserRegionSnapshot s)
    {
        _divisions = s.Entities.Where(e => e.EntityTypeId == RegionEntityTypes.Division && e.ParentEntityId is null)
            .ToDictionary(e => e.EntityId);
        _groups = s.Entities.Where(e => e.EntityTypeId == RegionEntityTypes.Group
                && e.ParentEntityId is { } parent && _divisions.ContainsKey(parent))
            .ToDictionary(e => e.EntityId);
        _orphanGroups = s.Entities.Where(e => e.EntityTypeId == RegionEntityTypes.Group && !_groups.ContainsKey(e.EntityId))
            .ToDictionary(e => e.EntityId);
        _countries = s.Countries.ToDictionary(c => c.CountryId);
        _countriesByGroup = s.Countries.Where(c => c.GroupEntityId is not null).ToLookup(c => c.GroupEntityId!.Value);
        _ticked = s.TickedEntityIds.ToHashSet();
        _granted = s.GrantedCountryIds.ToHashSet();
    }

    public static RegionTree From(UserRegionSnapshot snapshot) => new(snapshot);

    // ---------- lookups (region administration) ----------

    public bool IsDivision(long entityId) => _divisions.ContainsKey(entityId);
    /// <summary>Any live group, placed or orphaned - an orphan can be re-parented, which is how it is repaired.</summary>
    public bool IsAnyGroup(long entityId) => _groups.ContainsKey(entityId) || _orphanGroups.ContainsKey(entityId);
    public bool IsPlacedGroup(long entityId) => _groups.ContainsKey(entityId);
    public RegionEntityRow? Entity(long entityId) =>
        _divisions.GetValueOrDefault(entityId) ?? _groups.GetValueOrDefault(entityId) ?? _orphanGroups.GetValueOrDefault(entityId);
    public RegionCountryRow? Country(long countryId) => _countries.GetValueOrDefault(countryId);
    public int GroupCount(long divisionId) => _groups.Values.Count(g => g.ParentEntityId == divisionId);
    public int CountryCount(long groupId) => _countriesByGroup[groupId].Count();

    /// <summary>Is a live entity of this type already called <paramref name="name"/> (trimmed, case-insensitive)?</summary>
    public bool NameTaken(int entityTypeId, string name, long? exceptEntityId)
    {
        IEnumerable<RegionEntityRow> sameType = entityTypeId == RegionEntityTypes.Division
            ? _divisions.Values
            : _groups.Values.Concat(_orphanGroups.Values);
        return sameType.Any(e => e.EntityId != exceptEntityId
            && string.Equals(e.EntityName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool IsPlaced(RegionCountryRow c) => c.GroupEntityId is { } g && _groups.ContainsKey(g);

    private IEnumerable<RegionEntityRow> GroupsOf(long divisionId) =>
        _groups.Values.Where(g => g.ParentEntityId == divisionId);

    private IEnumerable<RegionCountryRow> PlacedCountriesOf(long groupId) =>
        _groups.ContainsKey(groupId) ? _countriesByGroup[groupId] : [];

    // ---------- user assignment ----------

    /// <summary>Validates a user-region request against the tree and returns the rows to store.</summary>
    public (SortedSet<long> EntityIds, SortedSet<long> CountryIds) Resolve(SaveUserRegionsRequest request, HashSet<long> alreadyGranted)
    {
        long[] divisionIds = (request.DivisionIds ?? []).Distinct().ToArray();
        long[] groupIds = (request.GroupIds ?? []).Distinct().ToArray();
        long[] countryIds = (request.CountryIds ?? []).Distinct().ToArray();

        var problems = new List<string>();
        Report(problems, "Unknown division(s)", divisionIds.Where(id => !_divisions.ContainsKey(id)));
        Report(problems, "Unknown group(s), or group(s) not under a division", groupIds.Where(id => !_groups.ContainsKey(id)));
        Report(problems, "Unknown country(ies)", countryIds.Where(id => !_countries.ContainsKey(id)));
        Report(problems, "Country(ies) not in any group", countryIds.Where(id => _countries.TryGetValue(id, out var c) && !IsPlaced(c)));
        Report(problems, "Inactive country(ies) cannot be newly assigned", countryIds.Where(id =>
            _countries.TryGetValue(id, out var c) && IsPlaced(c) && !c.IsActive && !alreadyGranted.Contains(id)));
        if (problems.Count > 0) throw new BusinessRuleException(string.Join(" ", problems));

        var countries = new SortedSet<long>(countryIds);
        foreach (long divisionId in divisionIds)
            foreach (RegionEntityRow group in GroupsOf(divisionId))
                countries.UnionWith(ActiveCountryIds(group.EntityId));
        foreach (long groupId in groupIds)
            countries.UnionWith(ActiveCountryIds(groupId));

        // Division/group rows = exactly the ancestors of the granted countries.
        var entities = new SortedSet<long>();
        foreach (long countryId in countries)
        {
            long groupId = _countries[countryId].GroupEntityId!.Value;
            entities.Add(groupId);
            entities.Add(_groups[groupId].ParentEntityId!.Value);
        }
        return (entities, countries);
    }

    private IEnumerable<long> ActiveCountryIds(long groupId) =>
        PlacedCountriesOf(groupId).Where(c => c.IsActive).Select(c => c.CountryId);

    private static void Report(List<string> problems, string label, IEnumerable<long> ids)
    {
        long[] bad = ids.ToArray();
        if (bad.Length > 0) problems.Add($"{label}: {string.Join(", ", bad)}.");
    }

    public UserRegionsResponse ToUserResponse(long userProfileId)
    {
        var divisions = _divisions.Values.OrderBy(d => d.EntityName).Select(d =>
        {
            var groups = GroupsOf(d.EntityId).OrderBy(g => g.EntityName).Select(g =>
            {
                RegionCountryRow[] countries = PlacedCountriesOf(g.EntityId).OrderBy(c => c.CountryName).ToArray();
                return new RegionGroupNode(g.EntityId, g.EntityName, SelectionOf(countries), _ticked.Contains(g.EntityId),
                    countries.Select(UserNode).ToList());
            }).ToList();
            RegionCountryRow[] all = GroupsOf(d.EntityId).SelectMany(g => PlacedCountriesOf(g.EntityId)).ToArray();
            return new RegionDivisionNode(d.EntityId, d.EntityName, SelectionOf(all), _ticked.Contains(d.EntityId), groups);
        }).ToList();

        var unplaced = _countries.Values.Where(c => !IsPlaced(c)).OrderBy(c => c.CountryName).Select(UserNode).ToList();
        return new UserRegionsResponse(userProfileId, divisions, _granted.Order().ToList(), unplaced);
    }

    private RegionCountryNode UserNode(RegionCountryRow c) => new(c.CountryId, c.CountryName, c.IsActive, _granted.Contains(c.CountryId));

    /// <summary>All = every active country held; Partial = some country held; None otherwise.</summary>
    private RegionSelection SelectionOf(IReadOnlyCollection<RegionCountryRow> countries)
    {
        int active = countries.Count(c => c.IsActive);
        if (active > 0 && countries.Count(c => c.IsActive && _granted.Contains(c.CountryId)) == active) return RegionSelection.All;
        return countries.Any(c => _granted.Contains(c.CountryId)) ? RegionSelection.Partial : RegionSelection.None;
    }

    // ---------- administration ----------

    public RegionTreeResponse ToAdminResponse()
    {
        RegionAdminGroup Group(RegionEntityRow g) => new(g.EntityId, g.EntityName, _divisions.ContainsKey(g.ParentEntityId ?? 0) ? g.ParentEntityId : null,
            _countriesByGroup[g.EntityId].OrderBy(c => c.CountryName).Select(AdminNode).ToList());

        var divisions = _divisions.Values.OrderBy(d => d.EntityName)
            .Select(d => new RegionAdminDivision(d.EntityId, d.EntityName, GroupsOf(d.EntityId).OrderBy(g => g.EntityName).Select(Group).ToList()))
            .ToList();
        var orphans = _orphanGroups.Values.OrderBy(g => g.EntityName).Select(Group).ToList();
        // Unplaced here = no group at all, or a group that does not exist (orphan groups list their own countries).
        var unplaced = _countries.Values.Where(c => c.GroupEntityId is not { } g || !IsAnyGroup(g))
            .OrderBy(c => c.CountryName).Select(AdminNode).ToList();
        return new RegionTreeResponse(divisions, orphans, unplaced);
    }

    private static RegionAdminCountry AdminNode(RegionCountryRow c) => new(c.CountryId, c.CountryName, c.IsActive);
}
