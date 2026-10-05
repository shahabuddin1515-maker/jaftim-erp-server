using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;
using Jaftim.Domain.Security;

namespace Jaftim.Application.Modules.Users;

/// <summary>
/// Replace a user's regions. Whole divisions and whole groups expand to every ACTIVE country under them at save time;
/// <paramref name="CountryIds"/> adds individual countries (a partial group). Everything empty clears the user's regions.
/// </summary>
/// <param name="DivisionIds">Divisions granted whole (EntityTypeId 1).</param>
/// <param name="GroupIds">Groups granted whole (EntityTypeId 2).</param>
/// <param name="CountryIds">Individual countries. Must belong to a group; an inactive one is accepted only if already granted.</param>
public sealed record SaveUserRegionsRequest(
    IReadOnlyList<long>? DivisionIds = null,
    IReadOnlyList<long>? GroupIds = null,
    IReadOnlyList<long>? CountryIds = null);

public sealed class SaveUserRegionsRequestValidator : AbstractValidator<SaveUserRegionsRequest>
{
    public SaveUserRegionsRequestValidator()
    {
        RuleFor(x => x.DivisionIds).Must(ids => ids is null || ids.Count <= 100).WithMessage("At most 100 divisions.");
        RuleFor(x => x.GroupIds).Must(ids => ids is null || ids.Count <= 500).WithMessage("At most 500 groups.");
        RuleFor(x => x.CountryIds).Must(ids => ids is null || ids.Count <= 1000).WithMessage("At most 1000 countries.");
        RuleForEach(x => x.DivisionIds).GreaterThan(0);
        RuleForEach(x => x.GroupIds).GreaterThan(0);
        RuleForEach(x => x.CountryIds).GreaterThan(0);
    }
}

/// <summary>How much of a division's or group's active countries the user holds.</summary>
public enum RegionSelection
{
    None,
    Partial,
    All,
}

/// <param name="CountryId">Base_Country_Id.</param>
/// <param name="Name">Country name.</param>
/// <param name="IsActive">Inactive countries grant no access; they cannot be newly assigned.</param>
/// <param name="IsGranted">Held in UserCountries - this is what scopes the user's data.</param>
public sealed record RegionCountryNode(long CountryId, string Name, bool IsActive, bool IsGranted);

/// <param name="EntityId">Entity.EntityId of the group.</param>
/// <param name="Name">Group name.</param>
/// <param name="Selection">All / Partial / None of the group's active countries.</param>
/// <param name="IsTicked">A UserEntities row exists - the legacy screen's checkbox (screen state, never access).</param>
/// <param name="Countries">The group's countries.</param>
public sealed record RegionGroupNode(long EntityId, string Name, RegionSelection Selection, bool IsTicked, IReadOnlyList<RegionCountryNode> Countries);

/// <param name="EntityId">Entity.EntityId of the division.</param>
/// <param name="Name">Division name.</param>
/// <param name="Selection">All / Partial / None of the active countries in all its groups.</param>
/// <param name="IsTicked">A UserEntities row exists (legacy screen state).</param>
/// <param name="Groups">The division's groups.</param>
public sealed record RegionDivisionNode(long EntityId, string Name, RegionSelection Selection, bool IsTicked, IReadOnlyList<RegionGroupNode> Groups);

/// <param name="UserProfileId">The user.</param>
/// <param name="Divisions">The full Division -> Group -> Country tree with this user's selection.</param>
/// <param name="GrantedCountryIds">Every country the user holds (UserCountries), placed or not.</param>
/// <param name="UnplacedCountries">Countries in no valid group - shown for completeness, not assignable.</param>
public sealed record UserRegionsResponse(
    long UserProfileId,
    IReadOnlyList<RegionDivisionNode> Divisions,
    IReadOnlyList<long> GrantedCountryIds,
    IReadOnlyList<RegionCountryNode> UnplacedCountries);

/// <summary>Raw rows of UserRegion_Get.</summary>
public sealed record UserRegionSnapshot(
    IReadOnlyList<RegionEntityRow> Entities,
    IReadOnlyList<RegionCountryRow> Countries,
    IReadOnlyList<long> TickedEntityIds,
    IReadOnlyList<long> GrantedCountryIds);

/// <summary>
/// The status row AssignEntitiesToUser returns instead of raising (200, or 500 + the SQL error after its rollback).
/// A class with setters because Dapper materialises it (docs/ARCHITECTURE.md "Domain").
/// </summary>
public sealed class RegionSaveResult
{
    public int StatusCode { get; set; }
    public string? Message { get; set; }
}

public interface IUserRegionRepository
{
    /// <summary>EXEC UserRegion_Get (database/v2/008).</summary>
    Task<UserRegionSnapshot> GetAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC AssignEntitiesToUser (legacy, unchanged): replaces UserEntities and UserCountries in one transaction.</summary>
    Task<RegionSaveResult> SaveAsync(long userProfileId, IReadOnlyCollection<long> entityIds, IReadOnlyCollection<long> countryIds, CancellationToken ct = default);
}

public interface IUserRegionService
{
    Task<UserRegionsResponse> GetAsync(long userProfileId, CancellationToken ct = default);
    Task<UserRegionsResponse> SaveAsync(long userProfileId, SaveUserRegionsRequest request, CancellationToken ct = default);
}

/// <summary>
/// Port of UserController.AssignEntities / UserEntityHierarchy (docs/USERS.md "Regions"). Access is decided by the
/// granted countries alone (fn_GetUserAccessibleCountries reads only UserCountries - owner decision 2026-10-05 keeps
/// it that way), so the service derives the country list from what was granted and writes the division/group rows
/// as exactly the ancestors of those countries. Ticks and access can then never disagree, and the legacy screen -
/// which enables a country only under a ticked group - renders the same selection. Because the Entity table has no
/// primary key, parent FK or shape constraint in the database, the shape is enforced here.
/// </summary>
public sealed class UserRegionService(
    IUserRegionRepository regions,
    IUserRepository users,
    IAuditWriter audit,
    IValidator<SaveUserRegionsRequest> validator) : IUserRegionService
{
    public async Task<UserRegionsResponse> GetAsync(long userProfileId, CancellationToken ct = default)
    {
        await EnsureUserAsync(userProfileId, ct);
        return RegionTree.From(await regions.GetAsync(userProfileId, ct)).ToResponse(userProfileId);
    }

    public async Task<UserRegionsResponse> SaveAsync(long userProfileId, SaveUserRegionsRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);
        UserProfileWithRole user = await EnsureUserAsync(userProfileId, ct);
        if (user.RoleId == RoleIds.Customer)
            throw new BusinessRuleException("Regions can only be assigned to staff.");

        UserRegionSnapshot before = await regions.GetAsync(userProfileId, ct);
        RegionTree tree = RegionTree.From(before);
        (SortedSet<long> entityIds, SortedSet<long> countryIds) = tree.Resolve(request, before.GrantedCountryIds.ToHashSet());

        RegionSaveResult result = await regions.SaveAsync(userProfileId, entityIds, countryIds, ct);
        if (result.StatusCode != 200)
            throw new InvalidOperationException($"AssignEntitiesToUser failed and rolled back: {result.Message}");

        UserRegionSnapshot after = await regions.GetAsync(userProfileId, ct);
        await audit.RecordAsync("UserProfile.RegionsChanged", "UserProfile", userProfileId,
            new { EntityIds = before.TickedEntityIds.Order(), CountryIds = before.GrantedCountryIds.Order() },
            new { EntityIds = entityIds, CountryIds = countryIds }, ct);
        return RegionTree.From(after).ToResponse(userProfileId);
    }

    private async Task<UserProfileWithRole> EnsureUserAsync(long userProfileId, CancellationToken ct) =>
        await users.GetByIdAsync(userProfileId, ct) is { } u && (u.IsDeleted ?? 0) == 0
            ? u
            : throw new NotFoundException("User", userProfileId);

    /// <summary>The valid Division -> Group -> Country tree of one snapshot. Rows that break the shape are left out.</summary>
    private sealed class RegionTree
    {
        private readonly Dictionary<long, RegionEntityRow> _divisions;
        private readonly Dictionary<long, RegionEntityRow> _groups;               // only groups under a valid division
        private readonly Dictionary<long, RegionCountryRow> _countries;           // every non-deleted country
        private readonly ILookup<long, RegionCountryRow> _countriesByGroup;       // placed countries only
        private readonly HashSet<long> _ticked;
        private readonly HashSet<long> _granted;

        private RegionTree(UserRegionSnapshot s)
        {
            _divisions = s.Entities.Where(e => e.EntityTypeId == RegionEntityTypes.Division && e.ParentEntityId is null)
                .ToDictionary(e => e.EntityId);
            _groups = s.Entities.Where(e => e.EntityTypeId == RegionEntityTypes.Group
                    && e.ParentEntityId is { } parent && _divisions.ContainsKey(parent))
                .ToDictionary(e => e.EntityId);
            _countries = s.Countries.ToDictionary(c => c.CountryId);
            _countriesByGroup = s.Countries.Where(IsPlaced).ToLookup(c => c.GroupEntityId!.Value);
            _ticked = s.TickedEntityIds.ToHashSet();
            _granted = s.GrantedCountryIds.ToHashSet();
        }

        public static RegionTree From(UserRegionSnapshot snapshot) => new(snapshot);

        private bool IsPlaced(RegionCountryRow c) => c.GroupEntityId is { } g && _groups.ContainsKey(g);

        private IEnumerable<RegionEntityRow> GroupsOf(long divisionId) =>
            _groups.Values.Where(g => g.ParentEntityId == divisionId);

        /// <summary>Validates the request against the tree and returns the rows to store.</summary>
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
            _countriesByGroup[groupId].Where(c => c.IsActive).Select(c => c.CountryId);

        private static void Report(List<string> problems, string label, IEnumerable<long> ids)
        {
            long[] bad = ids.ToArray();
            if (bad.Length > 0) problems.Add($"{label}: {string.Join(", ", bad)}.");
        }

        public UserRegionsResponse ToResponse(long userProfileId)
        {
            var divisions = _divisions.Values.OrderBy(d => d.EntityName).Select(d =>
            {
                var groups = GroupsOf(d.EntityId).OrderBy(g => g.EntityName).Select(g =>
                {
                    RegionCountryRow[] countries = _countriesByGroup[g.EntityId].OrderBy(c => c.CountryName).ToArray();
                    return new RegionGroupNode(g.EntityId, g.EntityName, SelectionOf(countries), _ticked.Contains(g.EntityId),
                        countries.Select(Node).ToList());
                }).ToList();
                RegionCountryRow[] all = GroupsOf(d.EntityId).SelectMany(g => _countriesByGroup[g.EntityId]).ToArray();
                return new RegionDivisionNode(d.EntityId, d.EntityName, SelectionOf(all), _ticked.Contains(d.EntityId), groups);
            }).ToList();

            var unplaced = _countries.Values.Where(c => !IsPlaced(c)).OrderBy(c => c.CountryName).Select(Node).ToList();
            return new UserRegionsResponse(userProfileId, divisions, _granted.Order().ToList(), unplaced);
        }

        private RegionCountryNode Node(RegionCountryRow c) => new(c.CountryId, c.CountryName, c.IsActive, _granted.Contains(c.CountryId));

        /// <summary>All = every active country held; Partial = some country held; None otherwise.</summary>
        private RegionSelection SelectionOf(IReadOnlyCollection<RegionCountryRow> countries)
        {
            int active = countries.Count(c => c.IsActive);
            if (active > 0 && countries.Count(c => c.IsActive && _granted.Contains(c.CountryId)) == active) return RegionSelection.All;
            return countries.Any(c => _granted.Contains(c.CountryId)) ? RegionSelection.Partial : RegionSelection.None;
        }
    }
}
