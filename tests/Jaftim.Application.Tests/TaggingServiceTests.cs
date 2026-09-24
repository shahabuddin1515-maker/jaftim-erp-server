using Jaftim.Application.Abstractions;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Application.Modules.Tagging;
using Jaftim.Domain.Entities.Tagging;
using Jaftim.Domain.Exceptions;

namespace Jaftim.Application.Tests;

public sealed class TaggingServiceTests
{
    [Fact]
    public async Task A_successful_tag_is_audited_and_the_agent_is_notified_about_the_customer()
    {
        var repo = new TaggingRepoFake();
        ITaggingService service = Build(repo, out AuditLog audit, out Notifications notify);

        await service.TagAsync(new TagCustomerRequest(99, 5));

        Assert.Equal([(99L, 5L)], repo.Tagged);
        Assert.Equal(["Customer.TaggedToAgent"], audit.Actions);
        NotificationRequest sent = Assert.Single(notify.Sent);
        Assert.Equal((NotificationTypeCodes.CustomerTagged, (long?)99), (sent.TypeCode, sent.EntityId));
        Assert.Equal([5L], sent.RecipientUserIds!);
    }

    [Theory]
    [InlineData("Limit Exceeded", "This agent has reached their customer tagging limit.")]
    [InlineData("Customer does not exists in your assigned divisions", "This customer is not in any of the agent's assigned divisions (countries).")]
    [InlineData("Something new", "Something new")]
    [InlineData(null, "The customer could not be tagged.")]
    public async Task A_rejection_returned_as_a_row_is_a_422_and_notifies_nobody(string? returnMsg, string expected)
    {
        // The legacy action notified the agent here too, because the procedure "succeeds" with a rejection text.
        var repo = new TaggingRepoFake { TagResult = returnMsg };
        ITaggingService service = Build(repo, out AuditLog audit, out Notifications notify);

        BusinessRuleException ex = await Assert.ThrowsAsync<BusinessRuleException>(() => service.TagAsync(new TagCustomerRequest(99, 5)));

        Assert.Equal(expected, ex.Message);
        Assert.Empty(audit.Actions);
        Assert.Empty(notify.Sent);
    }

    [Fact]
    public async Task Missing_ids_are_rejected_before_the_procedure_runs()
    {
        var repo = new TaggingRepoFake();
        ITaggingService service = Build(repo, out _, out _);

        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() => service.TagAsync(new TagCustomerRequest(0, 5)));
        await Assert.ThrowsAsync<Domain.Exceptions.ValidationException>(() => service.UntagAsync(new TagCustomerRequest(99, 0)));

        Assert.Empty(repo.Tagged);
        Assert.Empty(repo.Untagged);
    }

    [Fact]
    public async Task Untag_is_audited_and_notifies_the_agent_without_a_link()
    {
        var repo = new TaggingRepoFake();
        ITaggingService service = Build(repo, out AuditLog audit, out Notifications notify);

        await service.UntagAsync(new TagCustomerRequest(99, 5));

        Assert.Equal([(99L, 5L)], repo.Untagged);
        Assert.Equal(["Customer.UntaggedFromAgent"], audit.Actions);
        NotificationRequest sent = Assert.Single(notify.Sent);
        Assert.Equal(NotificationTypeCodes.CustomerUntagged, sent.TypeCode);
        Assert.Null(sent.Url);
    }

    [Fact]
    public async Task My_tagging_reads_the_callers_own_agent_id()
    {
        var repo = new TaggingRepoFake();
        ITaggingService service = Build(repo, out _, out _);

        await service.GetMyTaggedCustomersAsync();
        await service.GetMyUntagHistoryAsync();

        Assert.Equal([Caller.Id, Caller.Id], repo.AgentIdsRead);
    }

    internal static ITaggingService Build(TaggingRepoFake repo, out AuditLog audit, out Notifications notify)
    {
        audit = new AuditLog();
        notify = new Notifications();
        return new TaggingService(repo, audit, notify, new Caller(), new TagCustomerRequestValidator());
    }

    internal sealed class AuditLog : IAuditWriter
    {
        public List<string> Actions { get; } = [];
        public ValueTask RecordAsync(string action, string entityType, long? entityId, object? before, object? after, CancellationToken ct = default)
        { Actions.Add(action); return ValueTask.CompletedTask; }
    }

    internal sealed class Notifications : INotificationDispatcher
    {
        public List<NotificationRequest> Sent { get; } = [];
        public Task NotifyAsync(NotificationRequest request, CancellationToken ct = default) { Sent.Add(request); return Task.CompletedTask; }
    }

    private sealed class Caller : ICurrentUser
    {
        public const long Id = 131;
        public bool IsAuthenticated => true; public long UserProfileId => Id; public string? AccountId => "a"; public string? Email => "a@b.c";
        public string? FullName => "Agent"; public long RoleId => 2; public int UserTypeId => 2; public int CompanyId => 1;
    }
}

internal sealed class TaggingRepoFake : ITaggingRepository
{
    public string? TagResult { get; init; } = "Ok";
    public List<(long Customer, long Agent)> Tagged { get; } = [];
    public List<(long Customer, long Agent)> Untagged { get; } = [];
    public List<long> AgentIdsRead { get; } = [];

    public Task<IReadOnlyList<TaggingAgent>> GetAgentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaggingAgent>>([]);
    public Task<IReadOnlyList<TaggingCustomer>> GetTaggedCustomersAsync(long agentId, CancellationToken ct = default)
    { AgentIdsRead.Add(agentId); return Task.FromResult<IReadOnlyList<TaggingCustomer>>([]); }
    public Task<IReadOnlyList<TaggingCustomer>> GetUntagHistoryAsync(long agentId, CancellationToken ct = default)
    { AgentIdsRead.Add(agentId); return Task.FromResult<IReadOnlyList<TaggingCustomer>>([]); }
    public Task<IReadOnlyList<TaggingCustomer>> GetAvailableCustomersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaggingCustomer>>([]);
    public Task<string?> TagCustomerAsync(long customerId, long agentId, CancellationToken ct = default)
    { Tagged.Add((customerId, agentId)); return Task.FromResult(TagResult); }
    public Task UntagCustomerAsync(long customerId, long agentId, CancellationToken ct = default)
    { Untagged.Add((customerId, agentId)); return Task.CompletedTask; }
}
