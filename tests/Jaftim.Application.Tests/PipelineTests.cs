using System.Net.Mail;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Jobs;
using Jaftim.Application.Messaging;
using Jaftim.Application.Modules.Email;
using Jaftim.Application.Modules.Notifications;
using Jaftim.Application.Modules.Tenancy;
using Jaftim.Domain.Entities.Tenancy;
using Jaftim.Domain.Exceptions;
using Jaftim.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jaftim.Application.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task Steps_run_in_registration_order_around_the_terminal()
    {
        var trace = new List<string>();
        var pipeline = new Pipeline<List<string>>([new Recording("a"), new Recording("b")]);

        await pipeline.RunAsync(trace, _ => { trace.Add("terminal"); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(new[] { "a>", "b>", "terminal", "<b", "<a" }, trace);
    }

    [Fact]
    public async Task A_step_that_does_not_call_next_stops_the_run()
    {
        var trace = new List<string>();
        var pipeline = new Pipeline<List<string>>([new Recording("a"), new Stop(), new Recording("never")]);

        await pipeline.RunAsync(trace, _ => { trace.Add("terminal"); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(new[] { "a>", "stop", "<a" }, trace);
    }

    private sealed class Recording(string name) : IPipelineStep<List<string>>
    {
        public async Task InvokeAsync(List<string> context, PipelineStepDelegate next, CancellationToken ct)
        {
            context.Add($"{name}>");
            await next(ct);
            context.Add($"<{name}");
        }
    }

    private sealed class Stop : IPipelineStep<List<string>>
    {
        public Task InvokeAsync(List<string> context, PipelineStepDelegate next, CancellationToken ct)
        {
            context.Add("stop");
            return Task.CompletedTask;
        }
    }
}

public sealed class OutboxProcessorTests
{
    [Theory]
    [InlineData(1, 5, false, false, 30)]
    [InlineData(2, 5, false, false, 120)]
    [InlineData(4, 5, false, false, 1800)]
    [InlineData(5, 5, false, true, 0)]   // budget spent
    [InlineData(1, 5, true, true, 0)]    // permanent: no retry
    [InlineData(9, 20, false, false, 10800)] // past the schedule: the last delay repeats
    public void Retry_policy(int attempt, int max, bool permanent, bool deadLetter, int delay) =>
        Assert.Equal(new OutboxFailureDecision(deadLetter, delay), OutboxRetryPolicy.Decide(attempt, max, permanent));

    [Fact]
    public async Task Nothing_to_claim_runs_no_step_and_settles_nothing()
    {
        var store = new NotificationStore { Claimable = null };

        OutboxProcessResult result = await Processor(store).ProcessAsync(7, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.NotClaimed, result);
        Assert.Empty(store.Persisted);
        Assert.Empty(store.Settled);
    }

    [Fact]
    public async Task Success_persists_as_the_original_actor_pushes_and_records_the_notification_id()
    {
        var store = new NotificationStore { Claimable = Row(attempt: 1, actor: 42) };
        var pusher = new PusherFake();

        OutboxProcessResult result = await Processor(store, pusher).ProcessAsync(7, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.Succeeded, result);
        (NotificationRequest request, NotificationActor actor) = Assert.Single(store.Persisted);
        Assert.Equal(NotificationTypeCodes.CustomerTagged, request.TypeCode);
        Assert.Equal(42, actor.UserProfileId);       // not whoever runs the pipeline
        Assert.Equal(new[] { 11L, 12L }, Assert.Single(pusher.Pushed));
        Assert.Equal(("succeeded", 7L, (long?)900, false, 0), Assert.Single(store.Settled));
    }

    [Fact]
    public async Task A_row_an_earlier_attempt_already_persisted_is_not_persisted_or_pushed_again()
    {
        NotificationOutboxItem row = Row(attempt: 2);
        row.NotificationId = 555;
        var store = new NotificationStore { Claimable = row };
        var pusher = new PusherFake();

        OutboxProcessResult result = await Processor(store, pusher).ProcessAsync(7, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.Succeeded, result);
        Assert.Empty(store.Persisted);
        Assert.Empty(pusher.Pushed);
        Assert.Equal(("succeeded", 7L, (long?)555, false, 0), Assert.Single(store.Settled));
    }

    [Fact]
    public async Task A_transient_persist_failure_is_retried_with_back_off()
    {
        var store = new NotificationStore { Claimable = Row(attempt: 2), Fail = new TimeoutException("db busy") };

        OutboxProcessResult result = await Processor(store).ProcessAsync(7, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.RetryScheduled, result);
        Assert.Equal(("failed", 7L, (long?)null, false, 120), Assert.Single(store.Settled));
        Assert.Contains("db busy", store.LastError);
    }

    [Fact]
    public async Task A_permanent_failure_is_dead_lettered_at_once()
    {
        var store = new NotificationStore { Claimable = Row(attempt: 1), Fail = new BusinessRuleException("Unknown or inactive notification type code") };

        OutboxProcessResult result = await Processor(store).ProcessAsync(7, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.DeadLettered, result);
        Assert.True(Assert.Single(store.Settled).DeadLetter);
    }

    [Fact]
    public async Task The_last_allowed_attempt_failing_dead_letters()
    {
        var store = new NotificationStore { Claimable = Row(attempt: NotificationPipeline.MaxAttempts), Fail = new TimeoutException() };

        Assert.Equal(OutboxProcessResult.DeadLettered, await Processor(store).ProcessAsync(7, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_corrupt_payload_is_dead_lettered_without_calling_any_step()
    {
        NotificationOutboxItem row = Row(attempt: 1);
        row.PayloadJson = "{not json";
        var store = new NotificationStore { Claimable = row };

        Assert.Equal(OutboxProcessResult.DeadLettered, await Processor(store).ProcessAsync(7, null, CancellationToken.None));
        Assert.Empty(store.Persisted);
    }

    [Fact]
    public async Task A_push_failure_does_not_fail_the_message()
    {
        // The notification is already in the inbox; push is best-effort, as in legacy.
        var store = new NotificationStore { Claimable = Row(attempt: 1) };

        OutboxProcessResult result = await Processor(store, new PusherFake { Fail = true }).ProcessAsync(7, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.Succeeded, result);
    }

    [Fact]
    public async Task No_recipients_means_no_push()
    {
        var store = new NotificationStore { Claimable = Row(attempt: 1), Recipients = [] };
        var pusher = new PusherFake();

        await Processor(store, pusher).ProcessAsync(7, null, CancellationToken.None);

        Assert.Empty(pusher.Pushed);
    }

    [Fact]
    public async Task The_sweep_lease_is_passed_through_to_the_claim()
    {
        var store = new NotificationStore { Claimable = Row(attempt: 1) };
        Guid lease = Guid.NewGuid();

        await Processor(store).ProcessAsync(7, lease, CancellationToken.None);

        Assert.Equal(lease, store.ClaimedWithLease);
    }

    [Fact]
    public async Task The_dispatcher_queues_and_signals_and_never_throws()
    {
        var outbox = new NotificationStore();
        var signal = new SignalSpy();
        var dispatcher = new OutboxNotificationDispatcher(outbox, signal, NullLogger<OutboxNotificationDispatcher>.Instance);

        await dispatcher.NotifyAsync(new NotificationRequest(NotificationTypeCodes.CustomerTagged, EntityType: "Customer", EntityId: 5));
        Assert.Equal((OutboxKind.Notification, 501L), Assert.Single(signal.Signals));
        Assert.Equal(NotificationTypeCodes.CustomerTagged, NotificationPipeline.Deserialize(Assert.Single(outbox.Enqueued)).TypeCode);

        outbox.EnqueueFails = true;
        await dispatcher.NotifyAsync(new NotificationRequest(NotificationTypeCodes.CustomerTagged)); // swallowed, as in legacy
        Assert.Single(signal.Signals);
    }

    [Fact]
    public void The_payload_round_trips_with_recipient_lists()
    {
        var request = new NotificationRequest("BID_RAISED", "t", "m", "Stock", 9, "/stock/9", 2, [1, 2], [13], [7], UseRoleMap: false, ExcludeCreator: false);

        NotificationRequest back = NotificationPipeline.Deserialize(NotificationPipeline.Serialize(request));

        Assert.Equal((request.TypeCode, request.EntityId, request.UseRoleMap, request.ExcludeCreator), (back.TypeCode, back.EntityId, back.UseRoleMap, back.ExcludeCreator));
        Assert.Equal(new[] { 1L, 2L }, back.RecipientUserIds!);
        Assert.Equal(new[] { 13L }, back.RecipientRoleIds!);
        Assert.Equal(new[] { 7L }, back.ExcludeUserIds!);
    }

    private static NotificationOutboxProcessor Processor(NotificationStore store, PusherFake? pusher = null) =>
        new(store,
            new Pipeline<NotificationDelivery>(
            [
                new PersistNotificationStep(store, NullLogger<PersistNotificationStep>.Instance),
                new PushNotificationStep(pusher ?? new PusherFake(), NullLogger<PushNotificationStep>.Instance),
            ]),
            new DeliveryFailureClassifier(),
            NullLogger<NotificationOutboxProcessor>.Instance);

    private static NotificationOutboxItem Row(int attempt, long actor = 1) => new()
    {
        OutboxId = 7,
        LeaseId = Guid.NewGuid(),
        AttemptCount = attempt,
        MaxAttempts = NotificationPipeline.MaxAttempts,
        TypeCode = NotificationTypeCodes.CustomerTagged,
        PayloadJson = NotificationPipeline.Serialize(new NotificationRequest(NotificationTypeCodes.CustomerTagged, EntityType: "Customer", EntityId: 5)),
        CreatedBy = actor,
        CompanyId = 1,
        CreatedAtUtc = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc),
    };

    internal sealed class NotificationStore : INotificationOutboxRepository
    {
        public NotificationOutboxItem? Claimable { get; set; }
        public Guid? ClaimedWithLease { get; private set; }
        public bool EnqueueFails { get; set; }
        public Exception? Fail { get; init; }
        public IReadOnlyList<long> Recipients { get; init; } = [11, 12];
        public List<string> Enqueued { get; } = [];
        public List<(NotificationRequest, NotificationActor)> Persisted { get; } = [];
        public List<(string Kind, long Id, long? ResultId, bool DeadLetter, int Delay)> Settled { get; } = [];
        public string? LastError { get; private set; }

        public Task<long> EnqueueAsync(NotificationRequest request, string payloadJson, int maxAttempts, CancellationToken ct = default)
        {
            if (EnqueueFails) throw new TimeoutException("outbox down");
            Enqueued.Add(payloadJson);
            return Task.FromResult(500L + Enqueued.Count);
        }

        public Task<NotificationCreateResult> PersistAsync(NotificationOutboxItem item, NotificationRequest request, NotificationActor actor, CancellationToken ct = default)
        {
            if (Fail is not null) throw Fail;
            Persisted.Add((request, actor));
            var payload = new NotificationPayload { NotificationId = 900, TypeCode = request.TypeCode, Title = "Title", EntityType = request.EntityType, EntityId = request.EntityId, Priority = 1, CreatedAt = actor.OccurredAtUtc };
            return Task.FromResult(new NotificationCreateResult(payload, Recipients));
        }

        public Task<NotificationOutboxItem?> ClaimAsync(long outboxId, Guid? leaseId, CancellationToken ct = default)
        {
            ClaimedWithLease = leaseId;
            return Task.FromResult(Claimable);
        }

        public Task<IReadOnlyList<OutboxLease>> ClaimDueAsync(int batchSize, int leaseSeconds, int minAgeSeconds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OutboxLease>>([]);

        public Task<bool> MarkSucceededAsync(long outboxId, Guid leaseId, long? resultId, CancellationToken ct = default)
        {
            Settled.Add(("succeeded", outboxId, resultId, false, 0));
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(long outboxId, Guid leaseId, string error, bool deadLetter, int retryDelaySeconds, CancellationToken ct = default)
        {
            Settled.Add(("failed", outboxId, null, deadLetter, retryDelaySeconds));
            LastError = error;
            return Task.FromResult(true);
        }

        public Task<int> PurgeAsync(int retainDays, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class PusherFake : INotificationPusher
    {
        public bool Fail { get; init; }
        public List<IReadOnlyList<long>> Pushed { get; } = [];

        public Task PushAsync(NotificationPayload payload, IReadOnlyList<long> recipientUserIds, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("hub down");
            Pushed.Add(recipientUserIds);
            return Task.CompletedTask;
        }
    }

    internal sealed class SignalSpy : IOutboxSignal
    {
        public List<(OutboxKind, long)> Signals { get; } = [];
        public void Enqueued(OutboxKind kind, long outboxId) => Signals.Add((kind, outboxId));
    }
}

public sealed class EmailPipelineTests
{
    [Theory]
    [InlineData("someone@example.com", true)]
    [InlineData("971500123456", false)]                  // the lead pipeline's pseudo-address (phone digits)
    [InlineData("user@localhost", false)]                // no dotted domain
    [InlineData("Some One <someone@example.com>", false)] // display names are not stored addresses
    [InlineData("", false)]
    public void Deliverable_addresses(string address, bool expected) =>
        Assert.Equal(expected, EmailAddresses.IsDeliverable(address));

    [Fact]
    public async Task The_dispatcher_validates_trims_queues_and_signals()
    {
        var outbox = new EmailStore();
        var signal = new OutboxProcessorTests.SignalSpy();
        var dispatcher = new EmailDispatcher(outbox, signal, new EmailMessageValidator());

        await Assert.ThrowsAsync<ValidationException>(() => dispatcher.EnqueueAsync(new EmailMessage("971500123456", "Hi", "<p>x</p>")));
        Assert.Empty(outbox.Enqueued);

        long id = await dispatcher.EnqueueAsync(new EmailMessage(" someone@example.com ", "Hi", "<p>x</p>", "PasswordReset"));

        Assert.Equal("someone@example.com", Assert.Single(outbox.Enqueued).To);
        Assert.Equal((OutboxKind.Email, id), Assert.Single(signal.Signals));
    }

    [Fact]
    public async Task Suppress_mode_sends_nothing_and_settles_the_row_as_done()
    {
        var store = new EmailStore { Claimable = Row() };
        var sender = new SenderFake();

        OutboxProcessResult result = await Processor(store, sender, EmailDeliveryMode.Suppress).ProcessAsync(3, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.Succeeded, result);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Redirect_mode_delivers_to_the_redirect_address_and_names_the_real_recipient()
    {
        var store = new EmailStore { Claimable = Row() };
        var sender = new SenderFake();

        await Processor(store, sender, EmailDeliveryMode.Redirect, "qa@jaftim.test").ProcessAsync(3, null, CancellationToken.None);

        (string to, string subject) = Assert.Single(sender.Sent);
        Assert.Equal("qa@jaftim.test", to);
        Assert.Equal("[to: customer@example.com] Reset your password", subject);
    }

    [Fact]
    public async Task Redirect_without_a_valid_address_is_a_retryable_configuration_fault()
    {
        var store = new EmailStore { Claimable = Row() };
        var sender = new SenderFake();

        OutboxProcessResult result = await Processor(store, sender, EmailDeliveryMode.Redirect, "").ProcessAsync(3, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.RetryScheduled, result);   // fix the config and the message still goes out
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Send_mode_delivers_to_the_recipient()
    {
        var store = new EmailStore { Claimable = Row() };
        var sender = new SenderFake();

        await Processor(store, sender, EmailDeliveryMode.Send).ProcessAsync(3, null, CancellationToken.None);

        Assert.Equal(("customer@example.com", "Reset your password"), Assert.Single(sender.Sent));
    }

    [Fact]
    public async Task An_smtp_outage_is_retried_but_a_missing_mailbox_is_dead_lettered()
    {
        var transient = new EmailStore { Claimable = Row() };
        Assert.Equal(OutboxProcessResult.RetryScheduled,
            await Processor(transient, new SenderFake { Fail = new SmtpException(SmtpStatusCode.ServiceNotAvailable, "421") }, EmailDeliveryMode.Send)
                .ProcessAsync(3, null, CancellationToken.None));

        var permanent = new EmailStore { Claimable = Row() };
        Assert.Equal(OutboxProcessResult.DeadLettered,
            await Processor(permanent, new SenderFake { Fail = new SmtpFailedRecipientException(SmtpStatusCode.MailboxUnavailable, "customer@example.com") }, EmailDeliveryMode.Send)
                .ProcessAsync(3, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_undeliverable_stored_address_is_dead_lettered_without_sending()
    {
        EmailOutboxItem row = Row();
        row.ToAddress = "971500123456";
        var sender = new SenderFake();

        OutboxProcessResult result = await Processor(new EmailStore { Claimable = row }, sender, EmailDeliveryMode.Send).ProcessAsync(3, null, CancellationToken.None);

        Assert.Equal(OutboxProcessResult.DeadLettered, result);
        Assert.Empty(sender.Sent);
    }

    private static EmailOutboxProcessor Processor(EmailStore store, SenderFake sender, EmailDeliveryMode mode, string? redirectTo = null) =>
        new(store,
            new Pipeline<EmailDelivery>(
            [
                new EmailGuardStep(Options.Create(new EmailDeliveryOptions { Mode = mode, RedirectTo = redirectTo }), NullLogger<EmailGuardStep>.Instance),
                new SendEmailStep(sender),
            ]),
            new DeliveryFailureClassifier(),
            NullLogger<EmailOutboxProcessor>.Instance);

    private static EmailOutboxItem Row() => new()
    {
        OutboxId = 3,
        LeaseId = Guid.NewGuid(),
        AttemptCount = 1,
        MaxAttempts = EmailPipeline.MaxAttempts,
        ToAddress = "customer@example.com",
        Subject = "Reset your password",
        HtmlBody = "<p>link</p>",
        Category = "PasswordReset",
    };

    private sealed class EmailStore : IEmailOutboxRepository
    {
        public EmailOutboxItem? Claimable { get; init; }
        public List<EmailMessage> Enqueued { get; } = [];

        public Task<long> EnqueueAsync(EmailMessage message, int maxAttempts, CancellationToken ct = default)
        {
            Enqueued.Add(message);
            return Task.FromResult(800L + Enqueued.Count);
        }

        public Task<EmailOutboxItem?> ClaimAsync(long outboxId, Guid? leaseId, CancellationToken ct = default) => Task.FromResult(Claimable);
        public Task<IReadOnlyList<OutboxLease>> ClaimDueAsync(int batchSize, int leaseSeconds, int minAgeSeconds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OutboxLease>>([]);
        public Task<bool> MarkSucceededAsync(long outboxId, Guid leaseId, long? resultId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> MarkFailedAsync(long outboxId, Guid leaseId, string error, bool deadLetter, int retryDelaySeconds, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> PurgeAsync(int retainDays, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class SenderFake : IEmailSender
    {
        public Exception? Fail { get; init; }
        public List<(string To, string Subject)> Sent { get; } = [];

        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            if (Fail is not null) throw Fail;
            Sent.Add((to, subject));
            return Task.CompletedTask;
        }
    }
}

public sealed class JobPipelineTests
{
    [Fact]
    public async Task A_tenant_job_runs_with_its_tenant_bound()
    {
        var setter = new SetterSpy();
        var job = new JobContext("party-kind-sync", "jaftim");
        int? seen = null;

        await Runner(setter).RunAsync(job, _ => { seen = setter.TenantId; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal((1, "jaftim"), (setter.TenantId, setter.TenantCode));
        Assert.Equal(1, seen);              // bound before the body ran
        Assert.Equal(1, job.TenantId);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("acme")]   // inactive
    public async Task An_unknown_or_inactive_tenant_fails_the_run_before_the_body(string code)
    {
        bool ran = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(new SetterSpy()).RunAsync(new JobContext("x", code), _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None));

        Assert.False(ran);
    }

    [Fact]
    public async Task A_global_job_binds_no_tenant()
    {
        var setter = new SetterSpy();

        await Runner(setter).RunAsync(new JobContext("tenant-jobs-registrar", null), _ => Task.CompletedTask, CancellationToken.None);

        Assert.Null(setter.TenantCode);
    }

    [Fact]
    public async Task A_failing_body_is_rethrown_so_hangfire_records_the_failure()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            Runner(new SetterSpy()).RunAsync(new JobContext("x", "jaftim"), _ => throw new TimeoutException(), CancellationToken.None));
    }

    private static JobRunner Runner(SetterSpy setter) =>
        new(new Pipeline<JobContext>(
        [
            new JobLoggingStep(NullLogger<JobLoggingStep>.Instance),
            new JobTenantBindingStep(new Tenants(), setter),
        ]));

    private sealed class Tenants : ITenantRepository
    {
        private static readonly Tenant[] All =
        [
            new() { TenantId = 1, Code = "jaftim", IsActive = true },
            new() { TenantId = 2, Code = "acme", IsActive = false },
        ];

        public Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Tenant>>(All);
        public Task<Tenant?> GetByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(t => t.Code == code));
    }

    private sealed class SetterSpy : ITenantContextSetter
    {
        public int? TenantId { get; private set; }
        public string? TenantCode { get; private set; }
        public void Set(int tenantId, string tenantCode) => (TenantId, TenantCode) = (tenantId, tenantCode);
    }
}
