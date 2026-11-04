using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

/// <summary>
/// Texts through Twilio for the project chat, and only to numbers whose latest consent record is an opt-in (TCPA / A2P
/// 10DLC). Every text is logged in <see cref="Message"/> with its project, so a reply can be matched to the project
/// it answers.
/// </summary>
public sealed class SmsSender(AppDbContext db, IOptions<TwilioOptions> options, ILogger<SmsSender> logger)
{
    /// <summary>What a staff member confirms when they tick "agreed to receive texts" for a customer or team member.</summary>
    public const string StaffRecordedConsent =
        "Recorded by Joevar staff: agreed to receive SMS messages from Bumvar LLC dba Joevar regarding appointments, "
        + "scheduling, project updates, crew arrival notifications, permit/inspection updates, and other service-related "
        + "communications. Message frequency varies. Message and data rates may apply. Reply STOP to opt out or HELP for "
        + "assistance.";

    private const int MaxLength = 1500;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.Value.AccountSid) &&
        !string.IsNullOrWhiteSpace(options.Value.AuthToken) &&
        !string.IsNullOrWhiteSpace(options.Value.FromPhoneNumber);

    public string? FromNumber => options.Value.FromPhoneNumber;

    public static IQueryable<SmsConsentStatus?> LatestConsent(AppDbContext db, string number) =>
        db.SmsConsents
            .Where(consent => consent.PhoneNumber == number)
            .OrderByDescending(consent => consent.RecordedAt)
            .Select(consent => (SmsConsentStatus?)consent.Status)
            .Take(1);

    public Task<bool> IsOptedInAsync(string number, CancellationToken cancellationToken) =>
        LatestConsent(db, number).AnyAsync(status => status == SmsConsentStatus.OptedIn, cancellationToken);

    /// <summary>
    /// Records consent given (or withdrawn) as told to a staff member, when it differs from the latest record. Returns
    /// true when the number is newly opted in, so the caller can text the opt-in confirmation.
    /// </summary>
    public async Task<bool> RecordStaffConsentAsync(string number, bool optedIn, string recordedBy, CancellationToken cancellationToken)
    {
        var current = await LatestConsent(db, number).FirstOrDefaultAsync(cancellationToken);
        if ((current == SmsConsentStatus.OptedIn) == optedIn)
        {
            return false;
        }

        db.SmsConsents.Add(new SmsConsent
        {
            PhoneNumber = number,
            ContactPhoneId = await db.ContactPhones.Where(phone => phone.Number == number).Select(phone => (Guid?)phone.Id).FirstOrDefaultAsync(cancellationToken),
            Status = optedIn ? SmsConsentStatus.OptedIn : SmsConsentStatus.OptedOut,
            Source = SmsConsentSource.Verbal,
            Evidence = optedIn ? StaffRecordedConsent : "Recorded by Joevar staff: no longer wants texts.",
            SourceUrl = $"staff:{recordedBy}"
        });
        await db.SaveChangesAsync(cancellationToken);
        return optedIn;
    }

    /// <summary>The opt-in confirmation ("Reply STOP to unsubscribe"), sent once after consent is recorded.</summary>
    public Task<string?> SendOptInConfirmationAsync(string number, CancellationToken cancellationToken) =>
        SendAsync(number, options.Value.OptInMessage, null, checkConsent: false, cancellationToken);

    /// <summary>Texts one number. Returns null when Twilio accepted it, otherwise why it wasn't sent.</summary>
    public async Task<string?> SendAsync(string to, string body, Guid? projectId, bool checkConsent, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return "texting isn't set up";
        }

        if (checkConsent && !await IsOptedInAsync(to, cancellationToken))
        {
            return "hasn't agreed to texts";
        }

        var text = body.Length > MaxLength ? body[..(MaxLength - 1)] + "…" : body;
        try
        {
            TwilioClient.Init(options.Value.AccountSid, options.Value.AuthToken);
            var message = await MessageResource.CreateAsync(
                body: text,
                from: new PhoneNumber(options.Value.FromPhoneNumber),
                to: new PhoneNumber(to));
            db.Messages.Add(new Message
            {
                Direction = MessageDirection.Outbound,
                From = options.Value.FromPhoneNumber!,
                To = to,
                Body = text,
                TwilioSid = message.Sid,
                Status = message.Status?.ToString(),
                ProjectId = projectId
            });
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (ApiException exception)
        {
            logger.LogWarning(exception, "Twilio refused a project text. ErrorCode={ErrorCode}", exception.Code);
            // 21610: the recipient replied STOP.
            return exception.Code == 21610 ? "replied STOP to texts" : "Twilio couldn't send it";
        }
    }
}
