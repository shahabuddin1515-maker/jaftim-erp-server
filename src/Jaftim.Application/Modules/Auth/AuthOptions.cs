namespace Jaftim.Application.Modules.Auth;

/// <summary>Bound from configuration section "Auth".</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "jaftim-api";
    public string Audience { get; set; } = "jaftim-clients";
    /// <summary>HMAC-SHA256 signing key. Minimum 32 bytes. Store in Key Vault / user-secrets, never in appsettings.json.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
    public int MaxFailedAccessAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}
