using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Modules.Inquiries;
using Jaftim.Domain.Entities.Inquiries;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class InquiryServiceTests
{
    [Fact]
    public async Task List_is_validated_and_the_party_kind_filter_maps_to_the_legacy_IsContact_flag()
    {
        var repo = new InquiryRepoFake();
        IInquiryService service = Build(repo, out _, out _);

        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() => service.GetAllAsync(
            new InquiryListRequest { DateFrom = new DateOnly(2026, 5, 1), DateTo = new DateOnly(2026, 1, 1) }));

        await service.GetAllAsync(new InquiryListRequest { PartyKind = PartyKind.Contact });
        Assert.Equal(PartyKind.Contact, repo.LastRequest!.PartyKind);
    }

    [Fact]
    public async Task Single_inquiry_goes_through_the_list_so_row_visibility_still_applies()
    {
        var repo = new InquiryRepoFake();   // returns nothing -> the caller may not see it
        IInquiryService service = Build(repo, out _, out _);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetByIdAsync(42));
        Assert.Equal(42, repo.LastRequest!.InquiryId);
    }

    [Fact]
    public async Task Contact_status_needs_a_linked_party_then_saves_audits_and_re_qualifies()
    {
        var repo = new InquiryRepoFake { Item = new InquiryListItem { InquiryId = 7, UserProfileId = null } };
        IInquiryService unlinked = Build(repo, out _, out _);
        await Assert.ThrowsAsync<BusinessRuleException>(() => unlinked.SaveContactStatusAsync(7, new InquiryContactStatusRequest(3, "called")));

        var linked = new InquiryRepoFake { Item = new InquiryListItem { InquiryId = 7, UserProfileId = 99 } };
        IInquiryService service = Build(linked, out AuditSpy audit, out PartySpy party);

        await service.SaveContactStatusAsync(7, new InquiryContactStatusRequest(3, "called"));

        Assert.Equal((7L, 99L, 3), linked.SavedContactStatus);
        Assert.Single(audit.Actions, a => a == "Inquiry.ContactStatusSaved");
        Assert.Equal([99L], party.Refreshed);       // a completed record can now qualify as a customer
    }

    [Fact]
    public async Task Logging_an_interaction_defaults_the_time_and_returns_the_log()
    {
        var repo = new InquiryRepoFake();
        IInquiryService service = Build(repo, out AuditSpy audit, out _);

        await service.LogInteractionAsync(99, new LogInteractionRequest("WhatsApp", "asked for photos", null));

        Assert.Equal(FixedNow, repo.LoggedAt);
        Assert.Single(audit.Actions, a => a == "Customer.InteractionLogged");
        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() =>
            service.LogInteractionAsync(99, new LogInteractionRequest("", null, null)));
    }

    private static readonly DateTime FixedNow = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    private static IInquiryService Build(InquiryRepoFake repo, out AuditSpy audit, out PartySpy party)
    {
        audit = new AuditSpy();
        party = new PartySpy();
        return new InquiryService(repo, party, audit, new FixedClock(),
            new InquiryListRequestValidator(), new InquiryContactStatusRequestValidator(), new LogInteractionRequestValidator());
    }

    private sealed class FixedClock : IDateTimeProvider { public DateTime UtcNow => FixedNow; }

    private sealed class AuditSpy : IAuditWriter
    {
        public List<string> Actions { get; } = [];
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
        { Actions.Add(action); return ValueTask.CompletedTask; }
    }

    private sealed class PartySpy : IPartyService
    {
        public List<long> Refreshed { get; } = [];
        public Task<PartyClassification> GetClassificationAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult(new PartyClassification { UserProfileId = userProfileId });
        public Task<PartyClassification> RefreshClassificationAsync(long userProfileId, CancellationToken ct = default)
        { Refreshed.Add(userProfileId); return Task.FromResult(new PartyClassification { UserProfileId = userProfileId }); }
        public Task<PartyClassification> SetClassificationAsync(long userProfileId, SetPartyKindRequest request, CancellationToken ct = default) => Task.FromResult(new PartyClassification());
    }

    private sealed class InquiryRepoFake : IInquiryRepository
    {
        public InquiryListItem? Item { get; init; }
        public InquiryListRequest? LastRequest { get; private set; }
        public (long Inquiry, long Party, int Status)? SavedContactStatus { get; private set; }
        public DateTime? LoggedAt { get; private set; }

        public Task<PagedResult<InquiryListItem>> GetAllAsync(InquiryListRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            IReadOnlyList<InquiryListItem> items = Item is null ? [] : [Item];
            return Task.FromResult(new PagedResult<InquiryListItem>(items, request.Page, request.PageSize, items.Count));
        }
        public Task<IReadOnlyList<InquirySectionValue>> GetSectionsAsync(long inquiryId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InquirySectionValue>>([]);
        public Task<IReadOnlyList<InquirySectionValue>> GetPartySectionsAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InquirySectionValue>>([]);
        public Task<IReadOnlyList<InquiryListItem>> GetByPartyAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InquiryListItem>>([]);
        public Task SaveContactStatusAsync(long inquiryId, long userProfileId, int statusId, string? remarks, CancellationToken ct = default)
        { SavedContactStatus = (inquiryId, userProfileId, statusId); return Task.CompletedTask; }
        public Task TagToAgentAsync(long inquiryId, long agentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CustomerInteraction>> GetInteractionsAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CustomerInteraction>>([]);
        public Task LogInteractionAsync(long userProfileId, LogInteractionRequest request, DateTime contactedTimeUtc, CancellationToken ct = default)
        { LoggedAt = contactedTimeUtc; return Task.CompletedTask; }
        public Task<PartyClassification?> GetClassificationAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult<PartyClassification?>(new PartyClassification { UserProfileId = userProfileId });
        public Task<PartyClassification> RecomputeClassificationAsync(long userProfileId, CancellationToken ct = default) => Task.FromResult(new PartyClassification { UserProfileId = userProfileId });
        public Task<PartyClassification> SetClassificationAsync(long userProfileId, PartyKind kind, bool isManual, long actorUserProfileId, CancellationToken ct = default) =>
            Task.FromResult(new PartyClassification { UserProfileId = userProfileId, PartyKind = kind, PartyKindIsManual = isManual, Changed = true });
    }
}

public sealed class PartyServiceTests
{
    [Fact]
    public async Task Crossing_from_contact_to_customer_is_recorded_as_a_qualification_event()
    {
        var repo = new PartyRepoFake { Current = PartyKind.Contact, Recomputed = PartyKind.Customer, Changed = true };
        var audit = new AuditSpy();
        IPartyService service = new PartyService(repo, audit, new Actor(), new SetPartyKindRequestValidator());

        await service.RefreshClassificationAsync(5);

        Assert.Single(audit.Actions, a => a == "Customer.Qualified");
    }

    [Fact]
    public async Task An_unchanged_recompute_writes_no_audit_row()
    {
        var repo = new PartyRepoFake { Current = PartyKind.Customer, Recomputed = PartyKind.Customer, Changed = false };
        var audit = new AuditSpy();
        IPartyService service = new PartyService(repo, audit, new Actor(), new SetPartyKindRequestValidator());

        await service.RefreshClassificationAsync(5);

        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task Only_customer_role_parties_can_be_classified()
    {
        var repo = new PartyRepoFake { Current = null };   // staff profile: no kind
        IPartyService service = new PartyService(repo, new AuditSpy(), new Actor(), new SetPartyKindRequestValidator());

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.SetClassificationAsync(5, new SetPartyKindRequest(PartyKind.Customer)));
    }

    private sealed class Actor : ICurrentUser
    {
        public bool IsAuthenticated => true; public long UserProfileId => 1; public string? AccountId => "a"; public string? Email => "a@b.c";
        public string? FullName => "A"; public long RoleId => 1; public int UserTypeId => 1; public int CompanyId => 1;
    }

    private sealed class AuditSpy : IAuditWriter
    {
        public List<string> Actions { get; } = [];
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
        { Actions.Add(action); return ValueTask.CompletedTask; }
    }

    private sealed class PartyRepoFake : IInquiryRepository
    {
        public PartyKind? Current { get; init; }
        public PartyKind? Recomputed { get; init; }
        public bool Changed { get; init; }

        public Task<PartyClassification?> GetClassificationAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult<PartyClassification?>(new PartyClassification { UserProfileId = userProfileId, PartyKind = Current });
        public Task<PartyClassification> RecomputeClassificationAsync(long userProfileId, CancellationToken ct = default) =>
            Task.FromResult(new PartyClassification { UserProfileId = userProfileId, PartyKind = Recomputed, Changed = Changed, PartyQualifiedAtUtc = DateTime.UtcNow });
        public Task<PartyClassification> SetClassificationAsync(long userProfileId, PartyKind kind, bool isManual, long actorUserProfileId, CancellationToken ct = default) =>
            Task.FromResult(new PartyClassification { UserProfileId = userProfileId, PartyKind = kind, PartyKindIsManual = isManual, Changed = true });

        public Task<PagedResult<InquiryListItem>> GetAllAsync(InquiryListRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InquirySectionValue>> GetSectionsAsync(long inquiryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InquirySectionValue>> GetPartySectionsAsync(long userProfileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InquiryListItem>> GetByPartyAsync(long userProfileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveContactStatusAsync(long inquiryId, long userProfileId, int statusId, string? remarks, CancellationToken ct = default) => throw new NotSupportedException();
        public Task TagToAgentAsync(long inquiryId, long agentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomerInteraction>> GetInteractionsAsync(long userProfileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task LogInteractionAsync(long userProfileId, LogInteractionRequest request, DateTime contactedTimeUtc, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
