using System.Net;
using System.Net.Mail;
using Jaftim.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Jaftim.Infrastructure.Services;

/// <summary>Bound from "EmailSettings" - the same section name the legacy app used.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "EmailSettings";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Jaftim";
}

/// <summary>
/// Straight port of the legacy EmailService transport. Send email from a Hangfire job (IJobScheduler), not inline
/// in a request, so SMTP latency/failures never affect the API response.
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_options.Username, _options.Password),
        };
        using var message = new MailMessage(new MailAddress(_options.FromEmail, _options.FromName), new MailAddress(to))
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        await client.SendMailAsync(message, ct);
    }
}
