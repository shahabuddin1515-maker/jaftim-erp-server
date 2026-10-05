using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Users;

/// <param name="Name">Division name (unique among divisions, trimmed, case-insensitive; max 200).</param>
public sealed record SaveDivisionRequest(string Name);

/// <param name="Name">Group name (unique among groups, trimmed, case-insensitive; max 200).</param>
/// <param name="DivisionId">The division the group belongs to. Changing it moves the group with all its countries.</param>
public sealed record SaveGroupRequest(string Name, long DivisionId);

/// <param name="GroupId">The group the country moves to (must be under a division).</param>
public sealed record MoveCountryRequest(long GroupId);

public sealed class SaveDivisionRequestValidator : AbstractValidator<SaveDivisionRequest>
{
    public SaveDivisionRequestValidator() => RuleFor(x => x.Name).NotEmpty().Must(n => n.Trim().Length is > 0 and <= 200)
        .WithMessage("Name must be 1-200 characters.");
}

public sealed class SaveGroupRequestValidator : AbstractValidator<SaveGroupRequest>
{
    public SaveGroupRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Must(n => n.Trim().Length is > 0 and <= 200).WithMessage("Name must be 1-200 characters.");
        RuleFor(x => x.DivisionId).GreaterThan(0);
    }
}

public sealed class MoveCountryRequestValidator : AbstractValidator<MoveCountryRequest>
{
    public MoveCountryRequestValidator() => RuleFor(x => x.GroupId).GreaterThan(0);
}

/// <param name="CountryId">Base_Country_Id.</param>
/// <param name="Name">Country name.</param>
/// <param name="IsActive">Inactive countries grant no access.</param>
public sealed record RegionAdminCountry(long CountryId, string Name, bool IsActive);

/// <param name="EntityId">Entity.EntityId.</param>
/// <param name="Name">Group name.</param>
/// <param name="DivisionId">Its division; null for an orphan group (no or a missing division) - re-parent it to repair.</param>
/// <param name="Countries">Countries in the group.</param>
public sealed record RegionAdminGroup(long EntityId, string Name, long? DivisionId, IReadOnlyList<RegionAdminCountry> Countries);

/// <param name="EntityId">Entity.EntityId.</param>
/// <param name="Name">Division name.</param>
/// <param name="Groups">Its groups.</param>
public sealed record RegionAdminDivision(long EntityId, string Name, IReadOnlyList<RegionAdminGroup> Groups);

/// <param name="Divisions">The valid tree.</param>
/// <param name="OrphanGroups">Groups whose division is missing (the database does not prevent it) - fix with PUT .../groups/{id}.</param>
/// <param name="UnplacedCountries">Countries in no existing group - place with PUT .../countries/{id}/group.</param>
public sealed record RegionTreeResponse(
    IReadOnlyList<RegionAdminDivision> Divisions,
    IReadOnlyList<RegionAdminGroup> OrphanGroups,
    IReadOnlyList<RegionAdminCountry> UnplacedCountries);

/// <summary>database/v2/009 Region_* procedures. Each re-checks its own rules (THROW 50000 -> 422).</summary>
public interface IRegionAdminRepository
{
    /// <summary>EXEC Region_SaveEntity; null id creates. Returns the EntityId.</summary>
    Task<long> SaveEntityAsync(long? entityId, string name, int entityTypeId, long? parentEntityId, CancellationToken ct = default);
    /// <summary>EXEC Region_DeleteEntity (soft delete; empty regions only).</summary>
    Task DeleteEntityAsync(long entityId, CancellationToken ct = default);
    /// <summary>EXEC Region_MoveCountry. Returns how many users' ticks were re-derived.</summary>
    Task<int> MoveCountryAsync(long countryId, long groupEntityId, CancellationToken ct = default);
}

public interface IRegionAdminService
{
    Task<RegionTreeResponse> GetTreeAsync(CancellationToken ct = default);
    Task<RegionTreeResponse> CreateDivisionAsync(SaveDivisionRequest request, CancellationToken ct = default);
    Task<RegionTreeResponse> UpdateDivisionAsync(long divisionId, SaveDivisionRequest request, CancellationToken ct = default);
    Task<RegionTreeResponse> CreateGroupAsync(SaveGroupRequest request, CancellationToken ct = default);
    Task<RegionTreeResponse> UpdateGroupAsync(long groupId, SaveGroupRequest request, CancellationToken ct = default);
    Task<RegionTreeResponse> DeleteDivisionAsync(long divisionId, CancellationToken ct = default);
    Task<RegionTreeResponse> DeleteGroupAsync(long groupId, CancellationToken ct = default);
    Task<RegionTreeResponse> MoveCountryAsync(long countryId, MoveCountryRequest request, CancellationToken ct = default);
}

/// <summary>
/// Maintains the region tree (docs/USERS.md "Region administration"). The legacy app had no screen for this - it was
/// done by script. Validation happens here first for precise messages; the procedures repeat the integrity checks
/// under lock. Access (UserCountries) is never changed by any operation here.
/// </summary>
public sealed class RegionAdminService(
    IRegionAdminRepository repository,
    IUserRegionRepository regions,
    IAuditWriter audit,
    IValidator<SaveDivisionRequest> divisionValidator,
    IValidator<SaveGroupRequest> groupValidator,
    IValidator<MoveCountryRequest> moveValidator) : IRegionAdminService
{
    public async Task<RegionTreeResponse> GetTreeAsync(CancellationToken ct = default) =>
        (await TreeAsync(ct)).ToAdminResponse();

    public async Task<RegionTreeResponse> CreateDivisionAsync(SaveDivisionRequest request, CancellationToken ct = default)
    {
        await divisionValidator.ValidateAndThrowAppAsync(request, ct);
        RegionTree tree = await TreeAsync(ct);
        EnsureNameFree(tree, RegionEntityTypes.Division, request.Name, null);

        long id = await repository.SaveEntityAsync(null, request.Name.Trim(), RegionEntityTypes.Division, null, ct);
        await audit.RecordAsync("Region.DivisionCreated", "Entity", id, null, new { Name = request.Name.Trim() }, ct);
        return await GetTreeAsync(ct);
    }

    public async Task<RegionTreeResponse> UpdateDivisionAsync(long divisionId, SaveDivisionRequest request, CancellationToken ct = default)
    {
        await divisionValidator.ValidateAndThrowAppAsync(request, ct);
        RegionTree tree = await TreeAsync(ct);
        RegionEntityRow before = tree.IsDivision(divisionId) ? tree.Entity(divisionId)! : throw new NotFoundException("Division", divisionId);
        EnsureNameFree(tree, RegionEntityTypes.Division, request.Name, divisionId);

        await repository.SaveEntityAsync(divisionId, request.Name.Trim(), RegionEntityTypes.Division, null, ct);
        await audit.RecordAsync("Region.DivisionUpdated", "Entity", divisionId, new { Name = before.EntityName }, new { Name = request.Name.Trim() }, ct);
        return await GetTreeAsync(ct);
    }

    public async Task<RegionTreeResponse> CreateGroupAsync(SaveGroupRequest request, CancellationToken ct = default)
    {
        await groupValidator.ValidateAndThrowAppAsync(request, ct);
        RegionTree tree = await TreeAsync(ct);
        EnsureDivision(tree, request.DivisionId);
        EnsureNameFree(tree, RegionEntityTypes.Group, request.Name, null);

        long id = await repository.SaveEntityAsync(null, request.Name.Trim(), RegionEntityTypes.Group, request.DivisionId, ct);
        await audit.RecordAsync("Region.GroupCreated", "Entity", id, null, new { Name = request.Name.Trim(), request.DivisionId }, ct);
        return await GetTreeAsync(ct);
    }

    public async Task<RegionTreeResponse> UpdateGroupAsync(long groupId, SaveGroupRequest request, CancellationToken ct = default)
    {
        await groupValidator.ValidateAndThrowAppAsync(request, ct);
        RegionTree tree = await TreeAsync(ct);
        RegionEntityRow before = tree.IsAnyGroup(groupId) ? tree.Entity(groupId)! : throw new NotFoundException("Group", groupId);
        EnsureDivision(tree, request.DivisionId);
        EnsureNameFree(tree, RegionEntityTypes.Group, request.Name, groupId);

        await repository.SaveEntityAsync(groupId, request.Name.Trim(), RegionEntityTypes.Group, request.DivisionId, ct);
        await audit.RecordAsync("Region.GroupUpdated", "Entity", groupId,
            new { Name = before.EntityName, DivisionId = before.ParentEntityId }, new { Name = request.Name.Trim(), request.DivisionId }, ct);
        return await GetTreeAsync(ct);
    }

    public async Task<RegionTreeResponse> DeleteDivisionAsync(long divisionId, CancellationToken ct = default)
    {
        RegionTree tree = await TreeAsync(ct);
        RegionEntityRow division = tree.IsDivision(divisionId) ? tree.Entity(divisionId)! : throw new NotFoundException("Division", divisionId);
        if (tree.GroupCount(divisionId) > 0)
            throw new BusinessRuleException($"Division '{division.EntityName}' still has {tree.GroupCount(divisionId)} group(s); move or delete them first.");

        await repository.DeleteEntityAsync(divisionId, ct);
        await audit.RecordAsync("Region.DivisionDeleted", "Entity", divisionId, new { Name = division.EntityName }, null, ct);
        return await GetTreeAsync(ct);
    }

    public async Task<RegionTreeResponse> DeleteGroupAsync(long groupId, CancellationToken ct = default)
    {
        RegionTree tree = await TreeAsync(ct);
        RegionEntityRow group = tree.IsAnyGroup(groupId) ? tree.Entity(groupId)! : throw new NotFoundException("Group", groupId);
        if (tree.CountryCount(groupId) > 0)
            throw new BusinessRuleException($"Group '{group.EntityName}' still has {tree.CountryCount(groupId)} country(ies); move them to another group first.");

        await repository.DeleteEntityAsync(groupId, ct);
        await audit.RecordAsync("Region.GroupDeleted", "Entity", groupId, new { Name = group.EntityName, DivisionId = group.ParentEntityId }, null, ct);
        return await GetTreeAsync(ct);
    }

    public async Task<RegionTreeResponse> MoveCountryAsync(long countryId, MoveCountryRequest request, CancellationToken ct = default)
    {
        await moveValidator.ValidateAndThrowAppAsync(request, ct);
        RegionTree tree = await TreeAsync(ct);
        RegionCountryRow country = tree.Country(countryId) ?? throw new NotFoundException("Country", countryId);
        if (!tree.IsPlacedGroup(request.GroupId))
            throw new BusinessRuleException($"Group {request.GroupId} does not exist or is not under a division.");
        if (country.GroupEntityId == request.GroupId) return tree.ToAdminResponse();

        int affectedUsers = await repository.MoveCountryAsync(countryId, request.GroupId, ct);
        await audit.RecordAsync("Region.CountryMoved", "Country", countryId,
            new { GroupId = country.GroupEntityId }, new { request.GroupId, AffectedUsers = affectedUsers }, ct);
        return await GetTreeAsync(ct);
    }

    // UserRegion_Get with no user: the tree only.
    private async Task<RegionTree> TreeAsync(CancellationToken ct) => RegionTree.From(await regions.GetAsync(0, ct));

    private static void EnsureDivision(RegionTree tree, long divisionId)
    {
        if (!tree.IsDivision(divisionId)) throw new BusinessRuleException($"Division {divisionId} does not exist.");
    }

    private static void EnsureNameFree(RegionTree tree, int entityTypeId, string name, long? exceptEntityId)
    {
        if (tree.NameTaken(entityTypeId, name, exceptEntityId))
            throw new ConflictException($"A {(entityTypeId == RegionEntityTypes.Division ? "division" : "group")} named '{name.Trim()}' already exists.");
    }
}
