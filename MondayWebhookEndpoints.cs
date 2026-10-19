using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

public static class MondayWebhookEndpoints
{
    public static IEndpointRouteBuilder MapMondayWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/monday/webhook", HandleWebhookAsync)
            .WithName("ReceiveMondayWebhook")
            .WithSummary("Send a monday.com update to the customer as an SMS")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpRequest request,
        IOptions<MondayOptions> mondayOptions,
        IOptions<TwilioOptions> twilioOptions,
        MondayClient monday,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("MondayWebhook");
        var settings = mondayOptions.Value;
        var twilio = twilioOptions.Value;
        if (string.IsNullOrWhiteSpace(settings.WebhookSecret) ||
            !monday.IsConfigured ||
            string.IsNullOrWhiteSpace(twilio.AccountSid) ||
            string.IsNullOrWhiteSpace(twilio.AuthToken) ||
            string.IsNullOrWhiteSpace(twilio.FromPhoneNumber))
        {
            logger.LogError("Monday.com webhook settings are not fully configured.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Monday.com webhook is not configured.");
        }

        var token = request.Query["token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token),
                Encoding.UTF8.GetBytes(settings.WebhookSecret)))
        {
            logger.LogWarning("Rejected Monday.com webhook with an invalid token.");
            return Results.Unauthorized();
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        using (document)
        {
            var root = document.RootElement;

            // monday.com verifies a new webhook URL by expecting its challenge echoed back.
            if (root.TryGetProperty("challenge", out var challenge))
            {
                return Results.Json(new { challenge = challenge.GetString() });
            }

            if (!root.TryGetProperty("event", out var mondayEvent))
            {
                return Results.Ok();
            }

            var boardId = GetString(mondayEvent, "boardId");
            var itemId = GetString(mondayEvent, "pulseId");
            if (boardId is null || itemId is null)
            {
                return Results.Ok();
            }

            if (GetString(mondayEvent, "type") != "create_update")
            {
                return Results.Ok();
            }

            var updateId = GetString(mondayEvent, "updateId");
            var text = GetString(mondayEvent, "textBody") ?? StripHtml(GetString(mondayEvent, "body"));
            if (updateId is null || string.IsNullOrWhiteSpace(text))
            {
                return Results.Ok();
            }

            // Only new top-level updates addressed to "@Customer" are texted; replies in a thread are never sent.
            text = text.Trim();
            if (!text.StartsWith('@') ||
                GetString(mondayEvent, "replyId") is not null ||
                GetString(mondayEvent, "parentId") is not null)
            {
                return Results.Ok();
            }

            // Customer texts posted by this API also start with "@Customer"; never send those back.
            if (monday.IsPostedCustomerMessage(itemId, text))
            {
                return Results.Ok();
            }

            // Always answer 200 from here on: monday.com retries failed webhooks, which would resend the SMS.
            try
            {
                var customer = await monday.FindCustomerByBoardAsync(boardId, cancellationToken);
                if (customer is null || !MondayClient.TryGetMessageForCustomer(text, customer, out var message))
                {
                    // An @mention of a monday user, or a board without a customer phone.
                    return Results.Ok();
                }

                try
                {
                    TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
                    var sent = await MessageResource.CreateAsync(
                        body: message,
                        from: new PhoneNumber(twilio.FromPhoneNumber),
                        to: new PhoneNumber(customer.PhoneNumber));

                    logger.LogInformation("Sent Monday.com update to {Customer} as SMS {MessageSid}.", customer.Handle, sent.Sid);
                }
                catch (ApiException exception)
                {
                    logger.LogError(
                        exception,
                        "Twilio rejected the Monday.com update. StatusCode={StatusCode}, ErrorCode={ErrorCode}",
                        exception.Status,
                        exception.Code);
                    await monday.CreateUpdateAsync(
                        itemId,
                        $"❌ Not sent: Twilio error {exception.Code}: {exception.Message}",
                        updateId,
                        cancellationToken);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process Monday.com update {UpdateId}.", updateId);
            }

            return Results.Ok();
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static string? StripHtml(string? html) =>
        html is null ? null : WebUtility.HtmlDecode(HtmlTagRegex.Replace(html.Replace("<br>", "\n"), string.Empty));

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
}
