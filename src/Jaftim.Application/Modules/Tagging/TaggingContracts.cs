using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Tagging;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Modules.Tagging;

/// <summary>Tag or untag one customer to/from one agent (legacy CustomerTaggingController.TagCustomerToAgent / UnTagCustomerFromAgent).</summary>
/// <param name="CustomerId">The customer-role party (UserProfileId).</param>
/// <param name="AgentId">The staff member (UserProfileId).</param>
public sealed record TagCustomerRequest(long CustomerId, long AgentId);

public sealed class TagCustomerRequestValidator : AbstractValidator<TagCustomerRequest>
{
    public TagCustomerRequestValidator()
    {
        RuleFor(x => x.CustomerId).GreaterThan(0).WithMessage("Please select a customer.");
        RuleFor(x => x.AgentId).GreaterThan(0).WithMessage("Please select an agent.");
    }
}

public interface ITaggingRepository
{
    /// <summary>EXEC CustomerTagging_GetAllManagerAgent - roles 1/7 see every agent, everyone else only agents in their accessible countries.</summary>
    Task<IReadOnlyList<TaggingAgent>> GetAgentsAsync(CancellationToken ct = default);
    /// <summary>EXEC CustomerTagging_AgentTaggedCustomer - a Sales Executive (primary RoleId 2) gets rows only for themself.</summary>
    Task<IReadOnlyList<TaggingCustomer>> GetTaggedCustomersAsync(long agentId, CancellationToken ct = default);
    /// <summary>EXEC CustomerTagging_AgentUnTaggedCustomerHistory - same RoleId 2 restriction.</summary>
    Task<IReadOnlyList<TaggingCustomer>> GetUntagHistoryAsync(long agentId, CancellationToken ct = default);
    /// <summary>EXEC CustomerTagging_AvailableCustomers - every customer-role party with no active tag.</summary>
    Task<IReadOnlyList<TaggingCustomer>> GetAvailableCustomersAsync(CancellationToken ct = default);
    /// <summary>EXEC CustomerTagging_TagCustomerToAgent. Returns the procedure's returnMsg: "Ok" or a rejection text.</summary>
    Task<string?> TagCustomerAsync(long customerId, long agentId, CancellationToken ct = default);
    /// <summary>EXEC CustomerTagging_UnTagCustomerFromAgent - deactivates the (customer, agent) tag, if any. Always "Ok".</summary>
    Task UntagCustomerAsync(long customerId, long agentId, CancellationToken ct = default);
}

public interface ITaggingService
{
    Task<IReadOnlyList<TaggingAgent>> GetAgentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaggingCustomer>> GetTaggedCustomersAsync(long agentId, CancellationToken ct = default);
    Task<IReadOnlyList<TaggingCustomer>> GetUntagHistoryAsync(long agentId, CancellationToken ct = default);
    /// <summary>The caller's own tagged customers ("My Tagging").</summary>
    Task<IReadOnlyList<TaggingCustomer>> GetMyTaggedCustomersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaggingCustomer>> GetMyUntagHistoryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaggingCustomer>> GetAvailableCustomersAsync(CancellationToken ct = default);
    Task TagAsync(TagCustomerRequest request, CancellationToken ct = default);
    Task UntagAsync(TagCustomerRequest request, CancellationToken ct = default);
}

/// <summary>
/// The CUSTOMER_TAGGED / CUSTOMER_UNTAGGED payloads, shared by every path that tags (this module and the inquiry
/// screen). Mirrors the legacy NotificationDispatcher.CustomerTagged/CustomerUntagged; the URL is the legacy screen's
/// because the legacy app reads the same Notification table.
/// </summary>
public static class TaggingNotifications
{
    public static NotificationRequest Tagged(long customerId, long agentId) => new(
        NotificationTypeCodes.CustomerTagged,
        Message: "A customer has been tagged to you.",
        EntityType: "Customer", EntityId: customerId,
        Url: $"/Customer/CustomerDetail?userId={customerId}",
        RecipientUserIds: [agentId], UseRoleMap: false);

    public static NotificationRequest Untagged(long customerId, long agentId) => new(
        NotificationTypeCodes.CustomerUntagged,
        Message: "A customer has been untagged from you.",
        EntityType: "Customer", EntityId: customerId,
        Url: null,   // the agent no longer has access to the customer profile
        RecipientUserIds: [agentId], UseRoleMap: false);
}

public sealed class TaggingService(
    ITaggingRepository repository,
    IAuditWriter audit,
    INotificationDispatcher notifications,
    ICurrentUser currentUser,
    IValidator<TagCustomerRequest> validator) : ITaggingService
{
    /// <summary>The only success value of CustomerTagging_TagCustomerToAgent.</summary>
    private const string Ok = "Ok";

    public Task<IReadOnlyList<TaggingAgent>> GetAgentsAsync(CancellationToken ct = default) => repository.GetAgentsAsync(ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetTaggedCustomersAsync(long agentId, CancellationToken ct = default) =>
        repository.GetTaggedCustomersAsync(agentId, ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetUntagHistoryAsync(long agentId, CancellationToken ct = default) =>
        repository.GetUntagHistoryAsync(agentId, ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetMyTaggedCustomersAsync(CancellationToken ct = default) =>
        repository.GetTaggedCustomersAsync(currentUser.UserProfileId, ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetMyUntagHistoryAsync(CancellationToken ct = default) =>
        repository.GetUntagHistoryAsync(currentUser.UserProfileId, ct);

    public Task<IReadOnlyList<TaggingCustomer>> GetAvailableCustomersAsync(CancellationToken ct = default) =>
        repository.GetAvailableCustomersAsync(ct);

    /// <summary>
    /// The procedure reports its rejections ("Limit Exceeded", "Customer does not exists in your assigned divisions")
    /// as an ordinary result row, not an error. The legacy action notified the agent whenever the call completed -
    /// i.e. also for a rejected tag; v2 turns a rejection into a 422 and notifies only on "Ok".
    /// </summary>
    public async Task TagAsync(TagCustomerRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);

        string? result = await repository.TagCustomerAsync(request.CustomerId, request.AgentId, ct);
        if (!string.Equals(result?.Trim(), Ok, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException(DescribeRejection(result));

        await audit.RecordAsync("Customer.TaggedToAgent", "UserProfile", request.CustomerId,
            null, new { TaggedAgentId = request.AgentId }, ct);
        await notifications.NotifyAsync(TaggingNotifications.Tagged(request.CustomerId, request.AgentId), ct);
    }

    /// <summary>
    /// Like the legacy action, the agent is notified even when no active tag matched: the procedure reports "Ok"
    /// either way and offers no cheap existence check.
    /// </summary>
    public async Task UntagAsync(TagCustomerRequest request, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(request, ct);

        await repository.UntagCustomerAsync(request.CustomerId, request.AgentId, ct);
        await audit.RecordAsync("Customer.UntaggedFromAgent", "UserProfile", request.CustomerId,
            new { TaggedAgentId = request.AgentId }, new { TaggedAgentId = (long?)null }, ct);
        await notifications.NotifyAsync(TaggingNotifications.Untagged(request.CustomerId, request.AgentId), ct);
    }

    /// <summary>
    /// The procedure's texts, reworded: its division message says "your" but checks the AGENT's divisions, not the
    /// caller's. Anything unrecognised is passed through verbatim.
    /// </summary>
    internal static string DescribeRejection(string? returnMsg) => returnMsg?.Trim() switch
    {
        "Limit Exceeded" => "This agent has reached their customer tagging limit.",
        "Customer does not exists in your assigned divisions" => "This customer is not in any of the agent's assigned divisions (countries).",
        null or "" => "The customer could not be tagged.",
        var other => other,
    };
}
