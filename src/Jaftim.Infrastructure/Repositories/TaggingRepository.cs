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
    public Task<IReadOnlyList<TaggingAgent>> GetAgentsAsync(CancellationToken ct = default) =>
        db.QueryAsync<TaggingAgent>(SpCall.Procedure("CustomerTagging_GetAllManagerAgent"), ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetTaggedCustomersAsync(long agentId, CancellationToken ct = default) =>
        db.QueryAsync<TaggingCustomer>(SpCall.Procedure("CustomerTagging_AgentTaggedCustomer").With("@agentId", agentId), ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetUntagHistoryAsync(long agentId, CancellationToken ct = default) =>
        db.QueryAsync<TaggingCustomer>(SpCall.Procedure("CustomerTagging_AgentUnTaggedCustomerHistory").With("@agentId", agentId), ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetAvailableCustomersAsync(CancellationToken ct = default) =>
        db.QueryAsync<TaggingCustomer>(SpCall.Procedure("CustomerTagging_AvailableCustomers"), ct);

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
