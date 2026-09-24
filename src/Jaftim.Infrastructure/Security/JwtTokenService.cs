using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jaftim.Infrastructure.Security;

/// <summary>Claim names the API and any other consumer of the token agree on.</summary>
public static class JaftimClaims
{
    /// <summary>Catalog Account.AccountId (= the tenant AspNetUsers.Id it was seeded from).</summary>
    public const string Subject = JwtRegisteredClaimNames.Sub;
    /// <summary>Tenant.TenantId - selects the tenant database for every call made with this token.</summary>
    public const string TenantId = "tid";
    public const string TenantCode = "tcode";
    /// <summary>UserProfile.UserProfileId inside that tenant - what every SP receives as @CreatedBy.</summary>
    public const string UserProfileId = "uid";
    /// <summary>The PRIMARY role only; effective roles/permissions are resolved per request.</summary>
    public const string RoleId = "rid";
    public const string RoleName = "role";
    public const string UserTypeId = "utid";
    public const string CompanyId = "cid";
    public const string FullName = "name";
    public const string Email = JwtRegisteredClaimNames.Email;
    /// <summary>Account.TokenVersion at issue time; a mismatch means the token was revoked.</summary>
    public const string TokenVersion = "tv";
}

public sealed class JwtTokenService(IOptions<AuthOptions> options, IDateTimeProvider clock) : ITokenService
{
    private readonly AuthOptions _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AuthenticatedUser user, int tokenVersion)
    {
        DateTime now = clock.UtcNow;
        DateTime expires = now.AddMinutes(_options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JaftimClaims.Subject, user.AccountId),
                new Claim(JaftimClaims.TenantId, user.TenantId.ToString()),
                new Claim(JaftimClaims.TenantCode, user.TenantCode),
                new Claim(JaftimClaims.UserProfileId, user.UserProfileId.ToString()),
                new Claim(JaftimClaims.RoleId, user.RoleId.ToString()),
                new Claim(JaftimClaims.RoleName, user.RoleName),
                new Claim(JaftimClaims.UserTypeId, user.UserTypeId.ToString()),
                new Claim(JaftimClaims.CompanyId, user.CompanyId.ToString()),
                new Claim(JaftimClaims.FullName, user.FullName),
                new Claim(JaftimClaims.Email, user.Email),
                new Claim(JaftimClaims.TokenVersion, tokenVersion.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        };

        // Permissions are deliberately NOT in the token (150+ ids would bloat every request). They are resolved
        // server-side per request from the cached RoleActionMapping, so a rights change takes effect immediately.
        string token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
        return (token, expires);
    }

    public string CreateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    public static SymmetricSecurityKey SigningKey(AuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey) || Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
            throw new InvalidOperationException("Auth:SigningKey must be configured and at least 32 bytes long.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }
}
