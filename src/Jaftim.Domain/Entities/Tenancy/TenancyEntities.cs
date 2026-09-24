namespace Jaftim.Domain.Entities.Tenancy;

/// <summary>catalog dbo.Tenant - one row per company; each has its own database.</summary>
public sealed class Tenant
{
    public int TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Full connection string, or "kv:&lt;secret-name&gt;" resolved by the connection resolver in UAT/Live.</summary>
    public string ConnectionString { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
}

/// <summary>catalog dbo.Account - the shared credential. AccountId equals the tenant AspNetUsers.Id it was seeded from.</summary>
public sealed class Account
{
    public string AccountId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public bool LockoutEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public int TokenVersion { get; set; }
    /// <summary>Set once the API has changed the password; the catalog hash is then authoritative over tenant copies.</summary>
    public DateTime? PasswordChangedByApiAtUtc { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>catalog dbo.AccountTenant joined to Tenant - which UserProfile an account is inside a tenant database.</summary>
public sealed class AccountMembership
{
    public long AccountTenantId { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public string TenantCode { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public long UserProfileId { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
}
