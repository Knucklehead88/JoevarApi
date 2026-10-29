using System.Net;
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
        message.Body = BuildBody(body);

        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(settings.Username!, settings.Password!, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static readonly byte[] Logo = LoadLogo();

    private static byte[] LoadLogo()
    {
        using var stream = typeof(OfficeEmailSender).Assembly.GetManifestResourceStream("joevar-logo.png")
            ?? throw new InvalidOperationException("The embedded joevar-logo.png is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// The plain text as is, plus an HTML version with the logo on top. The logo is attached inline (cid:) rather
    /// than linked, so mail apps that block remote images still show it.
    /// </summary>
    private static MimeEntity BuildBody(string text)
    {
        var builder = new BodyBuilder { TextBody = text };
        var logo = builder.LinkedResources.Add("joevar-logo.png", Logo, new ContentType("image", "png"));
        logo.ContentId = MimeKit.Utils.MimeUtils.GenerateMessageId();

        var paragraphs = string.Concat(text.Trim().ReplaceLineEndings("\n").Split("\n\n").Select(paragraph =>
            $"<p style=\"margin:0 0 16px\">{WebUtility.HtmlEncode(paragraph.Trim('\n')).Replace("\n", "<br>")}</p>"));
        builder.HtmlBody = $"""
            <!doctype html>
            <html>
            <body style="margin:0;padding:0;background:#f4f6f5">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f4f6f5">
                <tr>
                  <td align="center" style="padding:24px 12px">
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:#ffffff;border-radius:12px">
                      <tr>
                        <td align="center" style="padding:28px 24px 8px">
                          <img src="cid:{logo.ContentId}" alt="Joevar" width="76" height="75" style="display:block;border:0">
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:16px 32px 16px;font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.5;color:#1f2a24">
                          {paragraphs}
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
        return builder.ToMessageBody();
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

    public string FromName { get; init; } = "Joevar";

    /// <summary>Where Contact form messages go, e.g. "office@joevar.com".</summary>
    public string? To { get; init; }
}
