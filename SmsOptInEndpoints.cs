using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

public static class SmsOptInEndpoints
{
    public static IEndpointRouteBuilder MapSmsOptInEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/sms/opt-in", SendOptInMessageAsync)
            .WithName("SendSmsOptInConfirmation")
            .WithSummary("Send an SMS opt-in confirmation")
            .Produces<SmsOptInResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<Results<Ok<SmsOptInResponse>, ProblemHttpResult>> SendOptInMessageAsync(
        SmsOptInRequest request,
        HttpContext httpContext,
        AppDbContext db,
        IOptions<TwilioOptions> twilioOptions,
        MondayClient monday,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SmsOptIn");
        var settings = twilioOptions.Value;
        if (string.IsNullOrWhiteSpace(settings.AccountSid) ||
            string.IsNullOrWhiteSpace(settings.AuthToken) ||
            string.IsNullOrWhiteSpace(settings.FromPhoneNumber))
        {
            logger.LogError("Twilio SMS settings are not configured.");
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "SMS service is not configured.");
        }

        // Proof of consent (TCPA / A2P 10DLC): who agreed to what wording, where, when and from which IP. Recorded
        // before the confirmation text, since the consent was given whether or not that text goes through.
        var contactPhoneId = await db.ContactPhones
            .Where(contactPhone => contactPhone.Number == request.PhoneNumber)
            .Select(contactPhone => (Guid?)contactPhone.Id)
            .FirstOrDefaultAsync(cancellationToken);
        db.SmsConsents.Add(new SmsConsent
        {
            PhoneNumber = request.PhoneNumber,
            ContactPhoneId = contactPhoneId,
            Status = SmsConsentStatus.OptedIn,
            Source = SmsConsentSource.WebForm,
            Evidence = request.ConsentText?.Trim(),
            EvidenceVersion = request.ConsentVersion?.Trim(),
            SourceUrl = request.ConsentSource?.Trim(),
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            TwilioClient.Init(settings.AccountSid, settings.AuthToken);

            var message = await MessageResource.CreateAsync(
                body: settings.OptInMessage,
                from: new PhoneNumber(settings.FromPhoneNumber),
                to: new PhoneNumber(request.PhoneNumber));

            if (monday.IsConfigured)
            {
                try
                {
                    var customer = await monday.FindCustomerByPhoneAsync(request.PhoneNumber, cancellationToken);
                    if (customer is not null)
                    {
                        await monday.CreateUpdateAsync(
                            customer.ItemId,
                            $"➡ Opt-in confirmation sent:{Environment.NewLine}{settings.OptInMessage}",
                            null,
                            cancellationToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Failed to log opt-in SMS {MessageSid} to Monday.com.", message.Sid);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return TypedResults.Ok(new SmsOptInResponse(message.Sid));
        }
        catch (ApiException exception)
        {
            var phoneSuffix = request.PhoneNumber.Length > 4
                ? request.PhoneNumber[^4..]
                : request.PhoneNumber;
            logger.LogError(
                exception,
                "Twilio rejected the opt-in SMS request. StatusCode={StatusCode}, ErrorCode={ErrorCode}, MoreInfo={MoreInfo}, RecipientLastFour={RecipientLastFour}",
                exception.Status,
                exception.Code,
                exception.MoreInfo,
                phoneSuffix);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "The SMS provider could not send the message.");
        }
    }
}

/// <summary>Payload containing the recipient's phone number in E.164 format.</summary>
public sealed record SmsOptInRequest
{
    [Required]
    [RegularExpression(@"^\+[1-9]\d{7,14}$", ErrorMessage = "PhoneNumber must use E.164 format.")]
    public required string PhoneNumber { get; init; }

    /// <summary>The exact consent wording shown next to the checkbox (stored as proof).</summary>
    [MaxLength(2000)]
    public string? ConsentText { get; init; }

    /// <summary>The version of that wording, e.g. "2026-10-8".</summary>
    [MaxLength(40)]
    public string? ConsentVersion { get; init; }

    /// <summary>The page the form was on.</summary>
    [MaxLength(500)]
    public string? ConsentSource { get; init; }
}

/// <summary>Details returned after Twilio accepts the opt-in message.</summary>
public sealed record SmsOptInResponse(string MessageSid);

public sealed class TwilioOptions
{
    public const string SectionName = "Twilio";

    public string? AccountSid { get; init; }

    public string? AuthToken { get; init; }

    public string? FromPhoneNumber { get; init; }

    public string? ForwardToPhoneNumber { get; init; }

    public string? InboundWebhookUrl { get; init; }

    /// <summary>Twilio Verify service (VA...) used for phone-number login codes.</summary>
    public string? VerifyServiceSid { get; init; }

    public string OptInMessage { get; init; } = "You are opted in to receive SMS messages. Reply STOP to unsubscribe.";
}