using Jaftim.Application.Modules.Auth;

namespace Jaftim.Application.Abstractions;

public interface ITokenService
{
    /// <summary>Signed JWT access token for the given principal.</summary>
    (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AuthenticatedUser user, int tokenVersion);
    /// <summary>Cryptographically random opaque refresh token (the raw value; only its hash is persisted).</summary>
    string CreateRefreshToken();
    string HashRefreshToken(string refreshToken);
}
