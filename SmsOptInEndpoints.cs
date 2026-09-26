using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
        IOptions<TwilioOptions> twilioOptions,
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

        try
        {
            TwilioClient.Init(settings.AccountSid, settings.AuthToken);

            var message = await MessageResource.CreateAsync(
                body: settings.OptInMessage,
                from: new PhoneNumber(settings.FromPhoneNumber),
                to: new PhoneNumber(request.PhoneNumber));

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

    public string OptInMessage { get; init; } = "You are opted in to receive SMS messages. Reply STOP to unsubscribe.";
}