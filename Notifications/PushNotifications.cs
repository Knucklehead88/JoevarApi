using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Web push settings: the VAPID key pair (base64url) and a contact for the push services.</summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    public string? PublicKey { get; set; }

    public string? PrivateKey { get; set; }

    /// <summary>"mailto:office@joevar.com": who push services contact about problems.</summary>
    public string Subject { get; set; } = "mailto:office@joevar.com";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>What a notification shows, and the page a click opens (on joevar.com).</summary>
public sealed record PushPayload(string Title, string Body, string Url, string Tag);

/// <summary>Who a notification is for: every staff device, or the devices that booked one visit.</summary>
public abstract record PushAudience
{
    public sealed record Staff : PushAudience;

    public sealed record Booking(Guid KitchenBookingId) : PushAudience;
}

/// <summary>
/// Notifications waiting to be sent. Requests only queue them (instantly); <see cref="PushSender"/> sends them in the
/// background, so a slow push service never delays a page.
/// </summary>
public sealed class PushQueue
{
    private readonly Channel<(PushAudience Audience, PushPayload Payload)> channel =
        Channel.CreateBounded<(PushAudience, PushPayload)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(PushAudience audience, PushPayload payload) => channel.Writer.TryWrite((audience, payload));

    public IAsyncEnumerable<(PushAudience Audience, PushPayload Payload)> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Sends queued notifications to every matching subscription, and forgets subscriptions the browser withdrew.</summary>
public sealed class PushSender(
    PushQueue queue,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<PushOptions> options,
    ILogger<PushSender> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            logger.LogWarning("Push notifications are off: set Push:PublicKey and Push:PrivateKey.");
        }

        await foreach (var (audience, payload) in queue.ReadAllAsync(stoppingToken))
        {
            if (!settings.IsConfigured)
            {
                continue;
            }

            try
            {
                await SendAsync(audience, payload, settings, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Push notification \"{Title}\" could not be sent.", payload.Title);
            }
        }
    }

    private async Task SendAsync(PushAudience audience, PushPayload payload, PushOptions settings, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var subscriptions = audience switch
        {
            PushAudience.Staff => db.WebPushSubscriptions.Where(subscription => subscription.UserId != null),
            PushAudience.Booking booking => db.WebPushSubscriptions.Where(subscription => subscription.KitchenBookingId == booking.KitchenBookingId),
            _ => db.WebPushSubscriptions.Where(_ => false)
        };

        var client = new PushServiceClient(httpClientFactory.CreateClient(nameof(PushSender)))
        {
            DefaultAuthentication = new VapidAuthentication(settings.PublicKey, settings.PrivateKey) { Subject = settings.Subject }
        };
        var content = JsonSerializer.Serialize(payload, JsonOptions);
        var delivered = 0;

        foreach (var subscription in await subscriptions.ToListAsync(cancellationToken))
        {
            var target = new PushSubscription { Endpoint = subscription.Endpoint };
            target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
            target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
            try
            {
                await client.RequestPushMessageDeliveryAsync(
                    target,
                    new PushMessage(content) { Topic = payload.Tag, Urgency = PushMessageUrgency.High, TimeToLive = 86400 },
                    cancellationToken);
                delivered++;
            }
            catch (PushServiceClientException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // The browser unsubscribed (or the subscription expired): stop sending to it.
                db.WebPushSubscriptions.Remove(subscription);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Push to one device failed for \"{Title}\".", payload.Title);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Push \"{Title}\" delivered to {Delivered} device(s).", payload.Title, delivered);
    }
}

/// <summary>The browser's PushSubscription (<c>subscription.toJSON()</c> flattened): where and how to send.</summary>
public sealed record PushSubscriptionRequest
{
    [Required]
    [MaxLength(1000)]
    public required string Endpoint { get; init; }

    [Required]
    [MaxLength(200)]
    public required string P256dh { get; init; }

    [Required]
    [MaxLength(100)]
    public required string Auth { get; init; }

    // Only the browsers' real push services: never let a client make the API post to an arbitrary address.
    private static readonly string[] PushServiceHosts =
        ["fcm.googleapis.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    public bool HasKnownPushService() =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && PushServiceHosts.Any(host => uri.Host == host || uri.Host.EndsWith($".{host}", StringComparison.Ordinal));
}
