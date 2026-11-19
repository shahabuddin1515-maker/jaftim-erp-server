using Jaftim.Application.Common;
using Jaftim.Application.Modules.Tagging;
using Jaftim.Domain.Entities.Tagging;
using Jaftim.Infrastructure.Data;

namespace Jaftim.Infrastructure.Repositories;

/// <summary>
/// CustomerTagging procedures, parameter lists copied from the legacy StockRepository/UserRepository call sites.
/// Row visibility (RoleId 2 sees only itself; non-admins see agents in their accessible countries) lives inside the
/// procedures and keys off @CreatedBy, which the executor supplies.
/// </summary>
public sealed class TaggingRepository(IDbExecutor db) : ITaggingRepository
{
    public Task<PagedResult<TaggingAgent>> GetAgentsAsync(PagedRequest paging, CancellationToken ct = default) =>
        db.QueryPagedAsync<TaggingAgent>(SpCall.Procedure("CustomerTagging_GetAllManagerAgent_V2"), paging, ct);

    public Task<PagedResult<TaggingCustomer>> GetTaggedCustomersAsync(long agentId, PagedRequest paging, CancellationToken ct = default) =>
        db.QueryPagedAsync<TaggingCustomer>(
            SpCall.Procedure("CustomerTagging_AgentCustomers_V2").With("@agentId", agentId).With("@Untagged", false), paging, ct);

    public Task<PagedResult<TaggingCustomer>> GetUntagHistoryAsync(long agentId, PagedRequest paging, CancellationToken ct = default) =>
        db.QueryPagedAsync<TaggingCustomer>(
            SpCall.Procedure("CustomerTagging_AgentCustomers_V2").With("@agentId", agentId).With("@Untagged", true), paging, ct);

    public Task<PagedResult<TaggingCustomer>> GetAvailableCustomersAsync(PagedRequest paging, CancellationToken ct = default) =>
        db.QueryPagedAsync<TaggingCustomer>(SpCall.Procedure("CustomerTagging_AvailableCustomers_V2"), paging, ct);

    public Task<string?> TagCustomerAsync(long customerId, long agentId, CancellationToken ct = default) =>
        db.QueryFirstOrDefaultAsync<string>(
            SpCall.Procedure("CustomerTagging_TagCustomerToAgent")
                .With("@customerId", customerId)
                .With("@agentId", agentId), ct);

    public Task UntagCustomerAsync(long customerId, long agentId, CancellationToken ct = default) =>
        db.ExecuteAsync(
            SpCall.Procedure("CustomerTagging_UnTagCustomerFromAgent")
                .With("@customerId", customerId)
                .With("@agentId", agentId), ct);
}
