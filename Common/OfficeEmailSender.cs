using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

/// <summary>
/// Sends emails to Joevar's office (Contact form messages, kitchen design bookings) and to customers (booking
/// confirmations) through Gmail's SMTP server,
/// logged in to a Google Workspace mailbox with an app password. The visitor is the Reply-To, so answering is just
/// "Reply". Callers save their record first and only log a send failure.
/// </summary>
public sealed class OfficeEmailSender(IOptions<EmailOptions> options, ILogger<OfficeEmailSender> logger)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.Username) &&
        !string.IsNullOrWhiteSpace(options.Value.Password) &&
        !string.IsNullOrWhiteSpace(options.Value.To);

    /// <summary>An email to the office; replying answers the visitor.</summary>
    public Task SendAsync(string subject, string body, string replyToName, string replyToEmail, CancellationToken cancellationToken) =>
        SendCoreAsync(null, subject, body, new MailboxAddress(replyToName, replyToEmail), cancellationToken);

    /// <summary>An email to a customer (e.g. a booking confirmation); replying reaches the office.</summary>
    public Task SendToCustomerAsync(string name, string email, string subject, string body, CancellationToken cancellationToken) =>
        SendCoreAsync(new MailboxAddress(name, email), subject, body, null, cancellationToken);

    /// <param name="to">The recipient, or null for the office (Email:To).</param>
    /// <param name="replyTo">Who replies go to, or null for the office.</param>
    private async Task SendCoreAsync(MailboxAddress? to, string subject, string body, MailboxAddress? replyTo, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            logger.LogWarning("\"{Subject}\" saved but not emailed: Email settings are not configured.", subject);
            return;
        }

        // IsConfigured guarantees Username, Password and To.
        var office = MailboxAddress.Parse(settings.To!);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, string.IsNullOrWhiteSpace(settings.From) ? settings.Username! : settings.From));
        message.To.Add(to ?? office);
        message.ReplyTo.Add(replyTo ?? office);
        message.Subject = subject.ReplaceLineEndings(" ").Trim();
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(settings.Username!, settings.Password!, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

/// <summary>Outgoing email to the office (Contact form, kitchen bookings). Password is a Google app password; keep it in user secrets.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; init; } = "smtp.gmail.com";

    public int Port { get; init; } = 587;

    /// <summary>The Google Workspace mailbox that sends, e.g. "office@joevar.com".</summary>
    public string? Username { get; init; }

    /// <summary>A 16-character Google app password for that mailbox (not the normal password).</summary>
    public string? Password { get; init; }

    /// <summary>The From address; defaults to Username (Gmail only allows the mailbox or its aliases).</summary>
    public string? From { get; init; }

    public string FromName { get; init; } = "Joevar website";

    /// <summary>Where Contact form messages go, e.g. "office@joevar.com".</summary>
    public string? To { get; init; }
}
