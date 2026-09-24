using System.Data;
using Jaftim.Application.Modules.Users;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories.Catalog;

/// <summary>Result of Catalog_Account_UpsertFromTenant for one tenant credential row.</summary>
public sealed class AccountUpsertResult
{
    public string AccountId { get; set; } = string.Empty;
    public string? CatalogPasswordHash { get; set; }
    /// <summary>True when the catalog is authoritative for this account and the tenant row holds a different hash.</summary>
    public bool MirrorToTenant { get; set; }
}

/// <summary>Catalog side of the AccountSyncJob (database/catalog/002_Seed_From_Tenant.md).</summary>
public interface IAccountSyncRepository
{
    Task<AccountUpsertResult> UpsertFromTenantAsync(TenantCredentialRow row, int tenantId, CancellationToken ct = default);
}

public sealed class AccountSyncRepository(ICatalogDbExecutor catalog) : IAccountSyncRepository
{
    public Task<AccountUpsertResult> UpsertFromTenantAsync(TenantCredentialRow row, int tenantId, CancellationToken ct = default) =>
        catalog.QuerySingleAsync<AccountUpsertResult>(
            SpCall.Procedure("Catalog_Account_UpsertFromTenant")
                .With("@AccountId", row.Id, DbType.String, 450)
                .With("@Email", row.Email, DbType.String, 256)
                .With("@PasswordHash", row.PasswordHash)
                .With("@LockoutEnabled", row.LockoutEnabled)
                .With("@LockoutEnd", row.LockoutEnd)
                .With("@AccessFailedCount", row.AccessFailedCount)
                .With("@TenantId", tenantId)
                .With("@UserProfileId", row.UserProfileId)
                .With("@IsActive", row.IsActive)
                .WithoutAudit(), ct);
}
