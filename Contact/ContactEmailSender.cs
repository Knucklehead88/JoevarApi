using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

/// <summary>
/// Emails each Contact form message to Joevar through Gmail's SMTP server, logged in to a Google Workspace mailbox
/// with an app password. The visitor is the Reply-To, so answering is just "Reply".
/// </summary>
public sealed class ContactEmailSender(IOptions<EmailOptions> options, ILogger<ContactEmailSender> logger)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.Username) &&
        !string.IsNullOrWhiteSpace(options.Value.Password) &&
        !string.IsNullOrWhiteSpace(options.Value.To);

    public async Task SendAsync(ContactMessage contact, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            logger.LogWarning("Contact message saved but not emailed: Email settings are not configured.");
            return;
        }

        // IsConfigured guarantees Username, Password and To.
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, string.IsNullOrWhiteSpace(settings.From) ? settings.Username! : settings.From));
        message.To.Add(MailboxAddress.Parse(settings.To!));
        message.ReplyTo.Add(new MailboxAddress(contact.Name, contact.Email));
        message.Subject = $"New contact message from {OneLine(contact.Name)}";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                New message from the Contact form on the Joevar website.

                Name:  {contact.Name}
                Email: {contact.Email}
                Phone: {contact.Phone ?? "-"}
                Sent:  {contact.CreatedAt:yyyy-MM-dd HH:mm} UTC

                {contact.Body}

                Reply to this email to answer {contact.Name}.
                """
        };

        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(settings.Username!, settings.Password!, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}

/// <summary>Outgoing email (the Contact form). Password is a Google app password; keep it in user secrets.</summary>
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
