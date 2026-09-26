using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Security;
using Twilio.Types;

public static class SmsInboundEndpoints
{
    public static IEndpointRouteBuilder MapSmsInboundEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/sms/inbound", ReceiveSmsAsync)
            .WithName("ReceiveInboundSms")
            .WithSummary("Receive and forward an inbound SMS")
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
        IOptions<MondayOptions> mondayOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SmsInbound");
        var twilio = twilioOptions.Value;
        if (string.IsNullOrWhiteSpace(twilio.AccountSid) ||
            string.IsNullOrWhiteSpace(twilio.AuthToken) ||
            string.IsNullOrWhiteSpace(twilio.InboundWebhookUrl) ||
            string.IsNullOrWhiteSpace(twilio.FromPhoneNumber) ||
            string.IsNullOrWhiteSpace(twilio.ForwardToPhoneNumber))
        {
            logger.LogError("Inbound Twilio SMS settings are not fully configured.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Inbound SMS forwarding is not configured.");
        }

        var monday = mondayOptions.Value;
        var mondayConfigured = !string.IsNullOrWhiteSpace(monday.ApiToken) &&
            !string.IsNullOrWhiteSpace(monday.BoardId);
        if (!mondayConfigured &&
            (!string.IsNullOrWhiteSpace(monday.ApiToken) || !string.IsNullOrWhiteSpace(monday.BoardId)))
        {
            logger.LogError("Monday.com integration requires both an API token and board ID.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Monday.com integration is not fully configured.");
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

        try
        {
            TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
            await MessageResource.CreateAsync(
                body: $"SMS from {sender}: {body}",
                from: new PhoneNumber(twilio.FromPhoneNumber),
                to: new PhoneNumber(twilio.ForwardToPhoneNumber));

            if (mondayConfigured)
            {
                await CreateMondayItemAsync(httpClientFactory, monday, sender, body, cancellationToken);
            }

            logger.LogInformation("Processed inbound SMS {MessageSid}.", messageSid);
            return Results.Content("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response />", "application/xml");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to process inbound SMS {MessageSid}.", messageSid);
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "The inbound SMS could not be forwarded.");
        }
    }

    private static async Task CreateMondayItemAsync(
        IHttpClientFactory httpClientFactory,
        MondayOptions settings,
        string sender,
        string body,
        CancellationToken cancellationToken)
    {
        const string query = "mutation ($boardId: ID!, $itemName: String!) { create_item(board_id: $boardId, item_name: $itemName) { id } }";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.monday.com/v2")
        {
            Content = JsonContent.Create(new
            {
                query,
                variables = new
                {
                    boardId = settings.BoardId,
                    itemName = $"SMS from {sender}: {body}"
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            throw new HttpRequestException("Monday.com returned a GraphQL error while creating the SMS item.");
        }
    }
}

public sealed class MondayOptions
{
    public const string SectionName = "Monday";

    public string? ApiToken { get; init; }

    public string? BoardId { get; init; }
}