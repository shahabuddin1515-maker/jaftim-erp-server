using System.Text.RegularExpressions;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Entities.Users;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Users;

public interface IPermissionKeyService
{
    /// <summary>
    /// Every RoleAction of a tenant as KEY -> ActionId, read live from the tenant database. Anonymous callers have no
    /// tenant, so it is chosen here: <paramref name="tenantCode"/> when given, otherwise the lowest-id active tenant.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetKeysAsync(string? tenantCode, CancellationToken ct = default);
}

public sealed partial class PermissionKeyService(
    ITenantRepository tenants,
    ITenantContextSetter tenantSetter,
    IUserRepository users) : IPermissionKeyService
{
    public async Task<IReadOnlyDictionary<string, int>> GetKeysAsync(string? tenantCode, CancellationToken ct = default)
    {
        Tenant tenant = await ResolveTenantAsync(tenantCode, ct);
        tenantSetter.Set(tenant.TenantId, tenant.Code);
        return BuildKeys(await users.GetAllRoleActionsAsync(ct));
    }

    private async Task<Tenant> ResolveTenantAsync(string? tenantCode, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tenantCode))
        {
            Tenant? byCode = await tenants.GetByCodeAsync(tenantCode.Trim(), ct);
            return byCode is { IsActive: true } ? byCode : throw new NotFoundException("Tenant", tenantCode);
        }

        return (await tenants.GetAllAsync(ct)).Where(t => t.IsActive).MinBy(t => t.TenantId)
            ?? throw new NotFoundException("Tenant", "default");
    }

    /// <summary>
    /// ActionName -> UPPER_SNAKE ("Document - Draft BL" -> DOCUMENT_DRAFT_BL), the same word split as
    /// tools/gen-permissions.ps1. Ordered by ActionId; a later duplicate name gets its id appended
    /// (526 "Customer Tagging" -> CUSTOMER_TAGGING, 550 -> CUSTOMER_TAGGING_550), so adding rows never renames a key.
    /// </summary>
    public static IReadOnlyDictionary<string, int> BuildKeys(IEnumerable<RoleAction> actions)
    {
        var keys = new Dictionary<string, int>();
        foreach (RoleAction action in actions.OrderBy(a => a.ActionId))
        {
            string key = ToKey(action.ActionName, action.ActionId);
            if (keys.ContainsKey(key)) key = $"{key}_{action.ActionId}";
            keys[key] = action.ActionId;
        }
        return keys;
    }

    private static string ToKey(string? name, int actionId)
    {
        string key = string.Join('_', NonAlphanumeric().Split(name ?? string.Empty).Where(w => w.Length > 0)).ToUpperInvariant();
        if (key.Length == 0) return $"ACTION_{actionId}";
        return char.IsDigit(key[0]) ? $"_{key}" : key;
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
