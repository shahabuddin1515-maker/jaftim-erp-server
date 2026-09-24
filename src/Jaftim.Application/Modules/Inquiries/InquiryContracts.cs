using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Jaftim.Application.Modules.Inquiries;

/// <summary>
/// Filters of the Inquiry list (EXEC GetInquiryAll). Row-level visibility is enforced inside the procedure from the
/// caller's PRIMARY role: RoleId 2 (Sales Executive) sees only inquiries tagged to them, RoleId 12 (CSD Manager)
/// sees their reporting subtree, everyone else sees all. Unchanged from the legacy screen.
/// </summary>
public sealed record InquiryListRequest : PagedRequest
{
    public int? InquiryId { get; init; }
    /// <summary>Customer/party id (the procedure calls it @CMID).</summary>
    public int? UserProfileId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Number { get; init; }
    public string? CreatedByName { get; init; }
    public int? CountryId { get; init; }
    public int? SourceId { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public int? LeadStatusId { get; init; }
    public bool? IsContacted { get; init; }
    /// <summary>Filter by party qualification: Contact (unqualified) or Customer (qualified).</summary>
    public PartyKind? PartyKind { get; init; }
    public bool? IsTagged { get; init; }
    public long? AgentId { get; init; }
    /// <summary>Follow-up bucket used by the legacy screen (see GetInquiryAll's @followUpStatus).</summary>
    public int? FollowUpStatus { get; init; }
}

public sealed class InquiryListRequestValidator : AbstractValidator<InquiryListRequest>
{
    public InquiryListRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.Name).MaximumLength(200);
        RuleFor(x => x.Email).MaximumLength(200);
        RuleFor(x => x.Number).MaximumLength(50);
        RuleFor(x => x.CreatedByName).MaximumLength(200);
        RuleFor(x => x.PartyKind).IsInEnum().When(x => x.PartyKind is not null);
        RuleFor(x => x).Must(x => x.DateFrom is null || x.DateTo is null || x.DateTo >= x.DateFrom)
            .WithMessage("dateTo must be on or after dateFrom.").WithName("dateTo");
    }
}

/// <summary>Records a contact attempt against an inquiry (EXEC Inquiry_ContactStatusSave).</summary>
public sealed record InquiryContactStatusRequest(int InquiryContactStatusId, string? Remarks);

public sealed class InquiryContactStatusRequestValidator : AbstractValidator<InquiryContactStatusRequest>
{
    public InquiryContactStatusRequestValidator()
    {
        RuleFor(x => x.InquiryContactStatusId).GreaterThan(0);
        RuleFor(x => x.Remarks).MaximumLength(4000);
    }
}

/// <summary>Logs an interaction with a party (EXEC CustomerContact_Save).</summary>
public sealed record LogInteractionRequest(string ContactType, string? ContactMessage, DateTime? ContactedTime);

public sealed class LogInteractionRequestValidator : AbstractValidator<LogInteractionRequest>
{
    public LogInteractionRequestValidator()
    {
        RuleFor(x => x.ContactType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ContactMessage).MaximumLength(4000);
    }
}

/// <summary>
/// Bulk tag (legacy TaggedFromInquiriesBulk). Keyed on the inquiry; the party is resolved server-side, so unlike the
/// legacy request the client does not send a CustomerId per row.
/// </summary>
public sealed record InquiryBulkTagRequest(long AgentId, IReadOnlyList<long>? InquiryIds);

/// <summary>
/// Bulk untag (legacy UnTagFromInquiriesBulk). Keyed on the (customer, agent) pair, NOT the inquiry - see
/// docs/INQUIRIES.md "Tagging, and why bulk tag and bulk untag are not symmetric".
/// </summary>
public sealed record InquiryBulkUntagRequest(IReadOnlyList<InquiryBulkUntagItem>? Items);

public sealed record InquiryBulkUntagItem(long CustomerId, long AgentId);

/// <summary>Outcome of a bulk tag/untag. Partial success is the contract: only "nothing succeeded" fails the request.</summary>
public sealed record BulkTagResult(int Requested, int Succeeded, int Failed, string Message);

public sealed class InquiryBulkTagRequestValidator : AbstractValidator<InquiryBulkTagRequest>
{
    public const int MaxItems = 1000;

    public InquiryBulkTagRequestValidator()
    {
        RuleFor(x => x.AgentId).GreaterThan(0).WithMessage("Please select an agent to tag the selected inquiries to.");
        RuleFor(x => x.InquiryIds).Must(ids => ids is not null && ids.Any(id => id > 0))
            .WithMessage("Please select at least one inquiry to tag.");
        RuleFor(x => x.InquiryIds!.Count).LessThanOrEqualTo(MaxItems).When(x => x.InquiryIds is not null).WithName("inquiryIds");
    }
}

public sealed class InquiryBulkUntagRequestValidator : AbstractValidator<InquiryBulkUntagRequest>
{
    public InquiryBulkUntagRequestValidator()
    {
        RuleFor(x => x.Items).Must(items => items is not null && items.Any(i => i.CustomerId > 0 && i.AgentId > 0))
            .WithMessage("None of the selected inquiries are currently tagged to an agent.");
        RuleFor(x => x.Items!.Count).LessThanOrEqualTo(InquiryBulkTagRequestValidator.MaxItems).When(x => x.Items is not null).WithName("items");
    }
}

public interface IInquiryRepository
{
    /// <summary>EXEC GetInquiryAll - page rows then the total count.</summary>
    Task<PagedResult<InquiryListItem>> GetAllAsync(InquiryListRequest request, CancellationToken ct = default);
    /// <summary>EXEC Inquiry_GetSectionById (the inquiry detail tabs).</summary>
    Task<IReadOnlyList<InquirySectionValue>> GetSectionsAsync(long inquiryId, CancellationToken ct = default);
    /// <summary>EXEC Contact_GetSectionById (party detail tabs, keyed by UserProfileId).</summary>
    Task<IReadOnlyList<InquirySectionValue>> GetPartySectionsAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC CustomerInquiries - every inquiry raised by one party.</summary>
    Task<IReadOnlyList<InquiryListItem>> GetByPartyAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC Inquiry_ContactStatusSave - appends to CustomerRemarks and refreshes the inquiry's cached latest values.</summary>
    Task SaveContactStatusAsync(long inquiryId, long userProfileId, int statusId, string? remarks, CancellationToken ct = default);
    /// <summary>EXEC Inquiry_TaggedFromInquiries - tag the inquiry's party to an agent.</summary>
    Task TagToAgentAsync(long inquiryId, long agentId, CancellationToken ct = default);
    /// <summary>EXEC CustomerTagging_UnTagCustomerFromAgent - deactivates the (customer, agent) tag, if any.</summary>
    Task UntagCustomerFromAgentAsync(long customerId, long agentId, CancellationToken ct = default);
    /// <summary>EXEC CustomerContact_GetByCustomerId / CustomerContact_Save (the interaction log).</summary>
    Task<IReadOnlyList<CustomerInteraction>> GetInteractionsAsync(long userProfileId, CancellationToken ct = default);
    Task LogInteractionAsync(long userProfileId, LogInteractionRequest request, DateTime contactedTimeUtc, CancellationToken ct = default);

    // ----- party classification (database/v2/005) -----
    Task<PartyClassification?> GetClassificationAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC Party_RecomputeKind - re-applies the qualification rule after a write.</summary>
    Task<PartyClassification> RecomputeClassificationAsync(long userProfileId, CancellationToken ct = default);
    /// <summary>EXEC Party_SetKindManual - pins (or releases) a human decision.</summary>
    Task<PartyClassification> SetClassificationAsync(long userProfileId, PartyKind kind, bool isManual, long actorUserProfileId, CancellationToken ct = default);
}

public interface IInquiryService
{
    Task<PagedResult<InquiryListItem>> GetAllAsync(InquiryListRequest request, CancellationToken ct = default);
    Task<InquiryListItem> GetByIdAsync(long inquiryId, CancellationToken ct = default);
    /// <summary>Party detail tabs (Contact_GetSectionById), grouped by section.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<InquirySectionValue>>> GetPartySectionsAsync(long userProfileId, CancellationToken ct = default);
    Task<IReadOnlyList<InquiryListItem>> GetByPartyAsync(long userProfileId, CancellationToken ct = default);
    Task<InquiryListItem> SaveContactStatusAsync(long inquiryId, InquiryContactStatusRequest request, CancellationToken ct = default);
    Task TagToAgentAsync(long inquiryId, long agentId, CancellationToken ct = default);
    Task<BulkTagResult> TagBulkAsync(InquiryBulkTagRequest request, CancellationToken ct = default);
    Task<BulkTagResult> UntagBulkAsync(InquiryBulkUntagRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerInteraction>> GetInteractionsAsync(long userProfileId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerInteraction>> LogInteractionAsync(long userProfileId, LogInteractionRequest request, CancellationToken ct = default);
}

public sealed class InquiryService(
    IInquiryRepository repository,
    IPartyService parties,
    IAuditWriter audit,
    IDateTimeProvider clock,
    INotificationDispatcher notifications,
    ILogger<InquiryService> logger,
    IValidator<InquiryListRequest> listValidator,
    IValidator<InquiryContactStatusRequest> contactStatusValidator,
    IValidator<LogInteractionRequest> interactionValidator,
    IValidator<InquiryBulkTagRequest> bulkTagValidator,
    IValidator<InquiryBulkUntagRequest> bulkUntagValidator) : IInquiryService
{
    public async Task<PagedResult<InquiryListItem>> GetAllAsync(InquiryListRequest request, CancellationToken ct = default)
    {
        await listValidator.ValidateAndThrowAppAsync(request, ct);
        return await repository.GetAllAsync(request, ct);
    }

    /// <summary>
    /// There is no single-inquiry procedure, and the list procedure carries the row-level visibility rules, so one
    /// inquiry is fetched through the same filtered query. A caller who may not see it gets 404, not someone else's row.
    /// </summary>
    public async Task<InquiryListItem> GetByIdAsync(long inquiryId, CancellationToken ct = default)
    {
        PagedResult<InquiryListItem> page = await repository.GetAllAsync(
            new InquiryListRequest { InquiryId = (int)inquiryId, Page = 1, PageSize = 1 }, ct);
        return page.Items.FirstOrDefault() ?? throw new NotFoundException("Inquiry", inquiryId);
    }

    /// <summary>
    /// Party detail tabs. NOT the inquiry's own sections: Inquiry_GetSectionById references Base_InquiryType, a
    /// table that does not exist in this database (verified on UAT too), so that procedure throws on every call and
    /// is not exposed. The inquiry's own fields are already served in full by GetByIdAsync.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<InquirySectionValue>>> GetPartySectionsAsync(long userProfileId, CancellationToken ct = default)
    {
        IReadOnlyList<InquirySectionValue> rows = await repository.GetPartySectionsAsync(userProfileId, ct);
        return rows.GroupBy(r => r.SectionName ?? string.Empty)
                   .ToDictionary(g => g.Key, g => (IReadOnlyList<InquirySectionValue>)g.ToList());
    }

    public Task<IReadOnlyList<InquiryListItem>> GetByPartyAsync(long userProfileId, CancellationToken ct = default) =>
        repository.GetByPartyAsync(userProfileId, ct);

    /// <summary>
    /// Appends a contact-status row (history lives in CustomerRemarks) and refreshes the inquiry's cached latest
    /// values. Because the enquirer's details may have been completed in the same conversation, the party's
    /// qualification is re-evaluated afterwards - that is how a Contact becomes a Customer.
    /// </summary>
    public async Task<InquiryListItem> SaveContactStatusAsync(long inquiryId, InquiryContactStatusRequest request, CancellationToken ct = default)
    {
        await contactStatusValidator.ValidateAndThrowAppAsync(request, ct);
        InquiryListItem before = await GetByIdAsync(inquiryId, ct);
        if (before.UserProfileId is not { } partyId)
            throw new BusinessRuleException("This inquiry is not linked to a party yet, so a contact status cannot be recorded.");

        await repository.SaveContactStatusAsync(inquiryId, partyId, request.InquiryContactStatusId, request.Remarks, ct);
        InquiryListItem after = await GetByIdAsync(inquiryId, ct);

        await audit.RecordAsync("Inquiry.ContactStatusSaved", "Inquiry", inquiryId,
            new { before.InquiryStatus, before.Remarks, before.IsContacted },
            new { after.InquiryStatus, after.Remarks, after.IsContacted }, ct);

        await parties.RefreshClassificationAsync(partyId, ct);
        return after;
    }

    /// <summary>
    /// An agent is tagged to the inquiry's PARTY. An unlinked inquiry is refused: Inquiry_TaggedFromInquiries would
    /// otherwise insert a CustomerTagging row with a NULL CustomerId (the column is nullable) and report "Ok".
    /// </summary>
    public async Task TagToAgentAsync(long inquiryId, long agentId, CancellationToken ct = default)
    {
        if (agentId <= 0)
            throw new Domain.Exceptions.ValidationException(new Dictionary<string, string[]> { ["agentId"] = ["Please select an agent."] });

        InquiryListItem before = await GetByIdAsync(inquiryId, ct);
        if (before.UserProfileId is not { } partyId)
            throw new BusinessRuleException("This inquiry is not linked to a party yet, so it cannot be tagged to an agent.");

        await repository.TagToAgentAsync(inquiryId, agentId, ct);
        await audit.RecordAsync("Inquiry.TaggedToAgent", "Inquiry", inquiryId,
            new { before.TaggedAgentId, before.Tagged }, new { TaggedAgentId = agentId }, ct);

        // Legacy NotificationDispatcher.CustomerTagged; the URL is the legacy screen's, which still reads this table.
        await notifications.NotifyAsync(new NotificationRequest(
            NotificationTypeCodes.CustomerTagged,
            Message: "A customer has been tagged to you.",
            EntityType: "Customer", EntityId: partyId,
            Url: $"/Customer/CustomerDetail?userId={partyId}",
            RecipientUserIds: [agentId], UseRoleMap: false), ct);
    }

    /// <summary>
    /// Legacy TaggedFromInquiriesBulk: one single-row tag per distinct inquiry, partially successful by design. Each
    /// item goes through <see cref="TagToAgentAsync"/>, so visibility (404), the unlinked-party guard, the audit row
    /// and the notification all apply per item.
    /// </summary>
    public async Task<BulkTagResult> TagBulkAsync(InquiryBulkTagRequest request, CancellationToken ct = default)
    {
        await bulkTagValidator.ValidateAndThrowAppAsync(request, ct);
        long[] inquiryIds = request.InquiryIds!.Where(id => id > 0).Distinct().ToArray();

        (int succeeded, int failed) = await RunEachAsync(inquiryIds, id => TagToAgentAsync(id, request.AgentId, ct),
            id => $"inquiry {id}", ct);

        if (succeeded == 0)
            throw new BusinessRuleException("None of the selected inquiries could be tagged.");
        string message = failed == 0
            ? $"{succeeded} {(succeeded == 1 ? "inquiry" : "inquiries")} tagged successfully."
            : $"{succeeded} of {inquiryIds.Length} inquiries tagged successfully. {failed} could not be tagged.";
        return new BulkTagResult(inquiryIds.Length, succeeded, failed, message);
    }

    /// <summary>
    /// Legacy UnTagFromInquiriesBulk: keyed on (customer, agent), so selected rows sharing a customer collapse into one
    /// call. Like the legacy action, the procedure reports "Ok" even when no active tag matched, and the agent is
    /// still notified - v2 has no cheap per-pair existence check to do better.
    /// </summary>
    public async Task<BulkTagResult> UntagBulkAsync(InquiryBulkUntagRequest request, CancellationToken ct = default)
    {
        await bulkUntagValidator.ValidateAndThrowAppAsync(request, ct);
        InquiryBulkUntagItem[] pairs = request.Items!.Where(i => i.CustomerId > 0 && i.AgentId > 0).Distinct().ToArray();

        (int succeeded, int failed) = await RunEachAsync(pairs, async pair =>
        {
            await repository.UntagCustomerFromAgentAsync(pair.CustomerId, pair.AgentId, ct);
            await audit.RecordAsync("Customer.UntaggedFromAgent", "UserProfile", pair.CustomerId,
                new { TaggedAgentId = pair.AgentId }, new { TaggedAgentId = (long?)null }, ct);
            await notifications.NotifyAsync(new NotificationRequest(
                NotificationTypeCodes.CustomerUntagged,
                Message: "A customer has been untagged from you.",
                EntityType: "Customer", EntityId: pair.CustomerId,
                Url: null,   // the agent no longer has access to the customer profile
                RecipientUserIds: [pair.AgentId], UseRoleMap: false), ct);
        }, pair => $"customer {pair.CustomerId} / agent {pair.AgentId}", ct);

        if (succeeded == 0)
            throw new BusinessRuleException("None of the selected inquiries could be untagged.");
        string message = failed == 0
            ? $"{succeeded} {(succeeded == 1 ? "customer" : "customers")} untagged successfully."
            : $"{succeeded} of {pairs.Length} customers untagged successfully. {failed} could not be untagged.";
        return new BulkTagResult(pairs.Length, succeeded, failed, message);
    }

    /// <summary>
    /// Runs every item, counting failures instead of stopping. When nothing succeeded and at least one failure was not
    /// an expected per-item rejection (an <see cref="AppException"/>), that fault is rethrown so an outage surfaces as
    /// a 500 rather than as "none could be tagged".
    /// </summary>
    private async Task<(int Succeeded, int Failed)> RunEachAsync<T>(IReadOnlyList<T> items, Func<T, Task> action, Func<T, string> describe, CancellationToken ct)
    {
        int succeeded = 0, failed = 0;
        Exception? unexpected = null;
        foreach (T item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await action(item);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                if (ex is not AppException)
                    unexpected = ex;
                logger.LogWarning(ex, "Bulk tag/untag failed for {Item}", describe(item));
            }
        }

        if (succeeded == 0 && unexpected is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(unexpected);
        return (succeeded, failed);
    }

    public Task<IReadOnlyList<CustomerInteraction>> GetInteractionsAsync(long userProfileId, CancellationToken ct = default) =>
        repository.GetInteractionsAsync(userProfileId, ct);

    public async Task<IReadOnlyList<CustomerInteraction>> LogInteractionAsync(long userProfileId, LogInteractionRequest request, CancellationToken ct = default)
    {
        await interactionValidator.ValidateAndThrowAppAsync(request, ct);
        await repository.LogInteractionAsync(userProfileId, request, request.ContactedTime ?? clock.UtcNow, ct);
        await audit.RecordAsync("Customer.InteractionLogged", "UserProfile", userProfileId, null,
            new { request.ContactType, request.ContactMessage }, ct);
        return await repository.GetInteractionsAsync(userProfileId, ct);
    }
}
