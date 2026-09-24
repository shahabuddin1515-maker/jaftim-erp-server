namespace Jaftim.Application.Abstractions;

/// <summary>
/// Hashes/verifies AspNetUsers.PasswordHash. The implementation MUST stay byte-compatible with ASP.NET Core
/// Identity's V3 format (PBKDF2-HMAC-SHA512, 100k iterations by default) so existing users keep their passwords
/// and the legacy MVC app can still log the same users in during the coexistence period.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    PasswordVerificationResult Verify(string hashedPassword, string providedPassword);
}

public enum PasswordVerificationResult
{
    Failed,
    Success,
    /// <summary>Verified, but hashed with older parameters - re-hash on next successful login.</summary>
    SuccessRehashNeeded,
}
