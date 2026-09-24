namespace Jaftim.Application.Modules.Auth;

/// <summary>Credentials plus an optional company choice.</summary>
/// <param name="Email">Login e-mail (case-insensitive).</param>
/// <param name="Password">Plain-text password; verified against the Identity-V3 hash.</param>
/// <param name="TenantCode">Required only when the account belongs to more than one company and none is marked default (see the 409 response).</param>
public sealed record LoginRequest(string Email, string Password, string? TenantCode = null);

/// <param name="RefreshToken">The opaque refresh token from a previous login/refresh/switch response.</param>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <param name="TenantCode">Code of a company listed in the caller's memberships.</param>
public sealed record SwitchTenantRequest(string TenantCode);

public sealed record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    AuthenticatedUser User);

/// <summary>A tenant the account can sign in to, and who they are inside it.</summary>
public sealed record TenantMembership(int TenantId, string TenantCode, string TenantName, long UserProfileId, bool IsDefault);

/// <summary>A role currently in effect for the user (primary or an active UserRole row).</summary>
public sealed record EffectiveRole(long RoleId, string RoleName, bool IsPrimary, DateTime? ValidFromUtc, DateTime? ValidToUtc);

/// <summary>What the frontend needs to render after login. Permissions are ActionIds (see Permissions catalog) - the union over all effective roles.</summary>
public sealed record AuthenticatedUser(
    string AccountId,
    int TenantId,
    string TenantCode,
    long UserProfileId,
    string Email,
    string FullName,
    long RoleId,
    string RoleName,
    int UserTypeId,
    int CompanyId,
    string? DefaultPath,
    bool RemoteAccessAllowed,
    IReadOnlyList<EffectiveRole> Roles,
    IReadOnlyCollection<int> Permissions,
    IReadOnlyList<TenantMembership> Memberships);

/// <summary>Request metadata the auth flow records to the auth audit log and refresh-token rows.</summary>
public sealed record ClientInfo(string? IpAddress, string? UserAgent);

/// <summary>Thrown (HTTP 409) when the account has several tenants and the request did not choose one.</summary>
public sealed class TenantSelectionRequiredException(IReadOnlyList<TenantMembership> memberships)
    : Domain.Exceptions.AppException("This account belongs to more than one company; specify tenantCode.")
{
    public IReadOnlyList<TenantMembership> Memberships { get; } = memberships;
}
