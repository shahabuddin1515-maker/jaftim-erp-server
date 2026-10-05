using System.Net.Mail;
using FluentValidation;
using Jaftim.Application.Abstractions;
using Jaftim.Application.Common;
using Jaftim.Application.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jaftim.Application.Modules.Email;

/// <summary>
/// The email pipeline - separate from notifications (own outbox, own queue, own settings):
/// <code>
///   IEmailDispatcher.EnqueueAsync -> EmailOutbox row -> signal
///   processor (Jobs host, Hangfire queue "email"):
///     1 EmailGuardStep  EmailDelivery:Mode - Suppress (default) / Redirect / Send
///     2 SendEmailStep   IEmailSender (SMTP)                 (retried with back-off; at-least-once)
/// </code>
/// Steps run in DI registration order (Application/DependencyInjection.cs).
/// </summary>
public static class EmailPipeline
{
    public const int MaxAttempts = 8;
}

/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="HtmlBody">Rendered HTML body.</param>
/// <param name="Category">Free-text label for monitoring, e.g. "PasswordReset".</param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string? Category = null);

public sealed class EmailMessageValidator : AbstractValidator<EmailMessage>
{
    public EmailMessageValidator()
    {
        RuleFor(x => x.To).NotEmpty().MaximumLength(320)
            .Must(EmailAddresses.IsDeliverable).WithMessage("'To' is not a deliverable e-mail address.");
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(400);
        RuleFor(x => x.HtmlBody).NotEmpty();
        RuleFor(x => x.Category).MaximumLength(100);
    }
}

public static class EmailAddresses
{
    /// <summary>
    /// A real mailbox, not one of the pseudo-addresses the lead pipeline stores (phone digits in the e-mail column -
    /// docs/INQUIRIES.md "Customer vs Contact"): parses, and has a dotted domain.
    /// </summary>
    public static bool IsDeliverable(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && MailAddress.TryCreate(address, out MailAddress? parsed)
        && parsed.Address == address.Trim()
        && parsed.Host.Contains('.');
}

/// <summary>Queues an e-mail. Unlike notifications this throws: the caller is told when nothing was queued.</summary>
public interface IEmailDispatcher
{
    /// <summary>Validates and queues the message; returns the EmailOutbox id. Invalid input is a ValidationException (400).</summary>
    Task<long> EnqueueAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>A claimed EmailOutbox row (columns of EmailOutbox_ClaimById).</summary>
public sealed class EmailOutboxItem : IOutboxItem
{
    public long OutboxId { get; set; }
    public Guid LeaseId { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string? Category { get; set; }
}

public interface IEmailOutboxRepository : IOutboxStore<EmailOutboxItem>
{
    /// <summary>EXEC EmailOutbox_Enqueue (audit trio injected as usual).</summary>
    Task<long> EnqueueAsync(EmailMessage message, int maxAttempts, CancellationToken ct = default);
}

public sealed class EmailDispatcher(IEmailOutboxRepository outbox, IOutboxSignal signal, IValidator<EmailMessage> validator) : IEmailDispatcher
{
    public async Task<long> EnqueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAppAsync(message, ct);
        long outboxId = await outbox.EnqueueAsync(message with { To = message.To.Trim() }, EmailPipeline.MaxAttempts, ct);
        signal.Enqueued(OutboxKind.Email, outboxId);
        return outboxId;
    }
}

public enum EmailDeliveryMode
{
    /// <summary>Log and drop (the default). Local and UAT databases are copies holding real customer addresses.</summary>
    Suppress,
    /// <summary>Deliver every message to <see cref="EmailDeliveryOptions.RedirectTo"/> instead of the recipient.</summary>
    Redirect,
    /// <summary>Deliver to the real recipient. Live only.</summary>
    Send,
}

/// <summary>Bound from "EmailDelivery". Defaults to <see cref="EmailDeliveryMode.Suppress"/> so no environment mails customers by accident.</summary>
public sealed class EmailDeliveryOptions
{
    public const string SectionName = "EmailDelivery";
    public EmailDeliveryMode Mode { get; set; } = EmailDeliveryMode.Suppress;
    public string? RedirectTo { get; set; }
}

public enum EmailOutcome
{
    Pending,
    Sent,
    Redirected,
    Suppressed,
}

/// <summary>The context the email steps share for one outbox row.</summary>
public sealed class EmailDelivery(EmailOutboxItem item) : IOutboxDeliveryContext
{
    public EmailOutboxItem Item { get; } = item;
    /// <summary>Where the message actually goes; <see cref="EmailGuardStep"/> may change it.</summary>
    public string DeliverTo { get; set; } = item.ToAddress;
    public string Subject { get; set; } = item.Subject;
    public EmailOutcome Outcome { get; set; } = EmailOutcome.Pending;
    public long? ResultId => null;
}

/// <summary>Step 1: the environment guard. Suppress short-circuits the pipeline (the row is marked succeeded).</summary>
public sealed class EmailGuardStep(IOptions<EmailDeliveryOptions> options, ILogger<EmailGuardStep> logger) : IPipelineStep<EmailDelivery>
{
    public Task InvokeAsync(EmailDelivery context, PipelineStepDelegate next, CancellationToken ct)
    {
        EmailDeliveryOptions o = options.Value;
        switch (o.Mode)
        {
            case EmailDeliveryMode.Send:
                return next(ct);

            case EmailDeliveryMode.Redirect:
                if (!EmailAddresses.IsDeliverable(o.RedirectTo))
                    throw new InvalidOperationException("EmailDelivery:Mode is Redirect but EmailDelivery:RedirectTo is not a valid address.");
                context.Subject = $"[to: {context.Item.ToAddress}] {context.Subject}";
                context.DeliverTo = o.RedirectTo!;
                context.Outcome = EmailOutcome.Redirected;
                return next(ct);

            default:
                context.Outcome = EmailOutcome.Suppressed;
                logger.LogInformation("Email outbox {OutboxId} ({Category}) suppressed by EmailDelivery:Mode", context.Item.OutboxId, context.Item.Category);
                return Task.CompletedTask;
        }
    }
}

/// <summary>Step 2: hand the message to SMTP. A failure is retried by the outbox with back-off.</summary>
public sealed class SendEmailStep(IEmailSender sender) : IPipelineStep<EmailDelivery>
{
    public async Task InvokeAsync(EmailDelivery context, PipelineStepDelegate next, CancellationToken ct)
    {
        await sender.SendAsync(context.DeliverTo, context.Subject, context.Item.HtmlBody, ct);
        if (context.Outcome == EmailOutcome.Pending) context.Outcome = EmailOutcome.Sent;
        await next(ct);
    }
}

public sealed class EmailOutboxProcessor(
    IEmailOutboxRepository store,
    Pipeline<EmailDelivery> pipeline,
    IDeliveryFailureClassifier classifier,
    ILogger<EmailOutboxProcessor> logger)
    : OutboxProcessor<EmailOutboxItem, EmailDelivery>(store, pipeline, classifier, logger)
{
    protected override OutboxKind Kind => OutboxKind.Email;

    protected override EmailDelivery CreateContext(EmailOutboxItem item) =>
        EmailAddresses.IsDeliverable(item.ToAddress)
            ? new EmailDelivery(item)
            : throw new PermanentDeliveryException($"'{item.ToAddress}' is not a deliverable e-mail address.");
}
