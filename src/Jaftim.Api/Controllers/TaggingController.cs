using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Modules.Tagging;
using Jaftim.Domain.Entities.Tagging;
using Jaftim.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Jaftim.Api.Controllers;

/// <summary>
/// Customer tagging (legacy CustomerTaggingController): which staff member (agent) looks after which customer.
/// A customer has at most one active tag; tagging a customer moves them off any previous agent. Tagging from an
/// inquiry row is a different path with different rules - see /api/inquiries/{id}/tag and docs/INQUIRIES.md.
/// </summary>
[Route("api/tagging")]
public sealed class TaggingController(ITaggingService tagging) : ApiControllerBase
{
    /// <summary>
    /// Agents with their active and all-time customer counts. Roles 1 and 7 see every agent; everyone else only
    /// agents in the countries they can access.
    /// </summary>
    [HttpGet("agents")]
    [HasPermission(Permissions.CustomerTaggingModule)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TaggingAgent>>>> GetAgents(CancellationToken ct) =>
        Ok(await tagging.GetAgentsAsync(ct));

    /// <summary>Customers currently tagged to an agent. A Sales Executive gets an empty list for anyone but themself.</summary>
    [HttpGet("agents/{agentId:long}/customers")]
    [HasPermission(Permissions.TaggedHistory)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TaggingCustomer>>>> GetTaggedCustomers(long agentId, CancellationToken ct) =>
        Ok(await tagging.GetTaggedCustomersAsync(agentId, ct));

    /// <summary>Customers untagged from an agent, with when and by whom. Same Sales Executive restriction.</summary>
    [HttpGet("agents/{agentId:long}/history")]
    [HasPermission(Permissions.UnTaggedHistory)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TaggingCustomer>>>> GetUntagHistory(long agentId, CancellationToken ct) =>
        Ok(await tagging.GetUntagHistoryAsync(agentId, ct));

    /// <summary>The caller's own tagged customers ("My Tagging").</summary>
    [HttpGet("me/customers")]
    [HasPermission(Permissions.MyTaggingAgent)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TaggingCustomer>>>> GetMyTaggedCustomers(CancellationToken ct) =>
        Ok(await tagging.GetMyTaggedCustomersAsync(ct));

    /// <summary>Customers untagged from the caller.</summary>
    [HttpGet("me/history")]
    [HasPermission(Permissions.MyTaggingAgent)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TaggingCustomer>>>> GetMyUntagHistory(CancellationToken ct) =>
        Ok(await tagging.GetMyUntagHistoryAsync(ct));

    /// <summary>Every customer-role party with no active tag (unpaged, as the legacy screen).</summary>
    [HttpGet("available-customers")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TaggingCustomer>>>> GetAvailableCustomers(CancellationToken ct) =>
        Ok(await tagging.GetAvailableCustomersAsync(ct));

    /// <summary>
    /// Tags a customer to an agent, replacing any existing tag. 422 when the agent is at their tagging limit or the
    /// customer is outside the agent's divisions (countries).
    /// </summary>
    [HttpPost("tag")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<object?>>> Tag([FromBody] TagCustomerRequest request, CancellationToken ct)
    {
        await tagging.TagAsync(request, ct);
        return Ok("Tagged.");
    }

    /// <summary>Removes the customer's tag to this agent (a no-op if there was none).</summary>
    [HttpPost("untag")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<object?>>> Untag([FromBody] TagCustomerRequest request, CancellationToken ct)
    {
        await tagging.UntagAsync(request, ct);
        return Ok("Untagged.");
    }
}
