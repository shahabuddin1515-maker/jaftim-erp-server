using Jaftim.Api.Contracts;
using Jaftim.Api.Security;
using Jaftim.Application.Common;
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
    // Every list below is paged, searched and sorted in SQL (database/v2/010). Customer-list search covers code, name,
    // e-mail, phone, WhatsApp, country, customer type and port (history also "untagged by"); customer-list SortBy:
    // code, fullName, countryName, customerType, portOfDischarge, modifiedAt, numberOfTimesTagged, unTaggedDate, unTaggedBy.

    /// <summary>
    /// Agents with their active and all-time customer counts. Roles 1 and 7 see every agent; everyone else only
    /// agents in the countries they can access. Search: code, name, e-mail, role, country, phone. SortBy: code,
    /// fullName, email, countryName, activeCustomerCount, totalCustomerCount, modifiedAt.
    /// </summary>
    [HttpGet("agents")]
    [HasPermission(Permissions.CustomerTaggingModule)]
    public async Task<ActionResult<ApiResponse<PagedResult<TaggingAgent>>>> GetAgents([FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await tagging.GetAgentsAsync(paging, ct));

    /// <summary>Customers currently tagged to an agent. A Sales Executive gets an empty list for anyone but themself.</summary>
    [HttpGet("agents/{agentId:long}/customers")]
    [HasPermission(Permissions.TaggedHistory)]
    public async Task<ActionResult<ApiResponse<PagedResult<TaggingCustomer>>>> GetTaggedCustomers(long agentId, [FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await tagging.GetTaggedCustomersAsync(agentId, paging, ct));

    /// <summary>Customers untagged from an agent, with when and by whom. Same Sales Executive restriction.</summary>
    [HttpGet("agents/{agentId:long}/history")]
    [HasPermission(Permissions.UnTaggedHistory)]
    public async Task<ActionResult<ApiResponse<PagedResult<TaggingCustomer>>>> GetUntagHistory(long agentId, [FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await tagging.GetUntagHistoryAsync(agentId, paging, ct));

    /// <summary>The caller's own tagged customers ("My Tagging").</summary>
    [HttpGet("me/customers")]
    [HasPermission(Permissions.MyTaggingAgent)]
    public async Task<ActionResult<ApiResponse<PagedResult<TaggingCustomer>>>> GetMyTaggedCustomers([FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await tagging.GetMyTaggedCustomersAsync(paging, ct));

    /// <summary>Customers untagged from the caller.</summary>
    [HttpGet("me/history")]
    [HasPermission(Permissions.MyTaggingAgent)]
    public async Task<ActionResult<ApiResponse<PagedResult<TaggingCustomer>>>> GetMyUntagHistory([FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await tagging.GetMyUntagHistoryAsync(paging, ct));

    /// <summary>Customer-role parties with no active tag.</summary>
    [HttpGet("available-customers")]
    [HasPermission(Permissions.CustomerTagging)]
    public async Task<ActionResult<ApiResponse<PagedResult<TaggingCustomer>>>> GetAvailableCustomers([FromQuery] PagedRequest paging, CancellationToken ct) =>
        Ok(await tagging.GetAvailableCustomersAsync(paging, ct));

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
