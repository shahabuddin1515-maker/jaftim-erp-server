namespace Jaftim.Domain.Entities.Users;

/// <summary>catalog dbo.AccountRefreshToken. Only the SHA-256 of the token is stored. A session is bound to one tenant.</summary>
public sealed class RefreshToken
{
    public long Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public long UserProfileId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive => RevokedAtUtc is null && ExpiresAtUtc > DateTime.UtcNow;
}
