namespace Jaftim.Application.Abstractions;

/// <summary>
/// The actor for the current unit of work. In the API it is built from the validated JWT; in Jobs it is the
/// configured system account. Every stored procedure receives <see cref="UserProfileId"/> as @CreatedBy and
/// <see cref="CompanyId"/> as @CompanyId, so this is also the identity the SPs use for row-level scoping.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    /// <summary>UserProfile.UserProfileId inside the current tenant - the id every SP and audit column uses.</summary>
    long UserProfileId { get; }
    /// <summary>Catalog Account.AccountId (JWT "sub"); equals the tenant AspNetUsers.Id the account was seeded from.</summary>
    string? AccountId { get; }
    string? Email { get; }
    string? FullName { get; }
    /// <summary>The PRIMARY role (UserProfile.RoleId). Additional/time-bound roles are resolved by IPermissionService.</summary>
    long RoleId { get; }
    int UserTypeId { get; }
    /// <summary>Legacy @CompanyId parameter value. Always 1 inside a tenant database (tenancy is per database).</summary>
    int CompanyId { get; }
}
