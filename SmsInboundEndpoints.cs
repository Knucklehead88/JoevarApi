using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Security;
using Twilio.Types;

public static class SmsInboundEndpoints
{
    private const string EmptyTwiml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response />";

    public static IEndpointRouteBuilder MapSmsInboundEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/sms/inbound", ReceiveSmsAsync)
            .WithName("ReceiveInboundSms")
            .WithSummary("Log an inbound SMS to monday.com")
            .Accepts<IFormCollection>("application/x-www-form-urlencoded")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> ReceiveSmsAsync(
        HttpRequest request,
        IOptions<TwilioOptions> twilioOptions,
        MondayClient monday,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SmsInbound");
        var twilio = twilioOptions.Value;
        if (string.IsNullOrWhiteSpace(twilio.AccountSid) ||
            string.IsNullOrWhiteSpace(twilio.AuthToken) ||
            string.IsNullOrWhiteSpace(twilio.InboundWebhookUrl) ||
            string.IsNullOrWhiteSpace(twilio.FromPhoneNumber) ||
            !monday.IsConfigured)
        {
            logger.LogError("Inbound SMS settings (Twilio or Monday.com) are not fully configured.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Inbound SMS logging is not configured.");
        }

        if (!request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var signature = request.Headers["X-Twilio-Signature"].ToString();
        var parameters = form.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToString(),
            StringComparer.Ordinal);

        if (!new RequestValidator(twilio.AuthToken).Validate(twilio.InboundWebhookUrl, parameters, signature))
        {
            logger.LogWarning("Rejected inbound SMS webhook with an invalid Twilio signature.");
            return Results.Unauthorized();
        }

        var sender = form["From"].ToString();
        var body = form["Body"].ToString();
        var messageSid = form["MessageSid"].ToString();
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(messageSid))
        {
            return Results.BadRequest();
        }

        var logText = new StringBuilder(body);
        if (int.TryParse(form["NumMedia"], out var mediaCount))
        {
            for (var index = 0; index < mediaCount; index++)
            {
                logText.Append($"{Environment.NewLine}📎 {form[$"MediaUrl{index}"]}");
            }
        }

        var fromForwardingNumber = string.Equals(sender, twilio.ForwardToPhoneNumber, StringComparison.Ordinal);
        try
        {
            var customer = await monday.FindCustomerByPhoneAsync(sender, cancellationToken);
            if (customer is not null)
            {
                await monday.CreateUpdateAsync(customer.ItemId, logText.ToString(), null, cancellationToken, customer.CustomerName);
            }
            else if (fromForwardingNumber)
            {
                // Replies to forwarded notifications come back to the Twilio number; they are not customer messages.
                logger.LogInformation("Ignored SMS {MessageSid} from the forwarding number; replies must be sent from monday.com.", messageSid);
                return Results.Content(EmptyTwiml, "application/xml");
            }
            else
            {
                logger.LogWarning("Inbound SMS {MessageSid} is from a number that is not on any customer board.", messageSid);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to log inbound SMS {MessageSid} to Monday.com.", messageSid);
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "The inbound SMS could not be logged.");
        }

        // No point notifying the forwarding phone about a text it sent itself.
        if (!string.IsNullOrWhiteSpace(twilio.ForwardToPhoneNumber) && !fromForwardingNumber)
        {
            try
            {
                TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
                await MessageResource.CreateAsync(
                    body: $"SMS from {sender}: {body}\n(Reply on monday.com)",
                    from: new PhoneNumber(twilio.FromPhoneNumber),
                    to: new PhoneNumber(twilio.ForwardToPhoneNumber));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to forward notification for inbound SMS {MessageSid}.", messageSid);
            }
        }

        logger.LogInformation("Processed inbound SMS {MessageSid}.", messageSid);
        return Results.Content(EmptyTwiml, "application/xml");
    }
}
