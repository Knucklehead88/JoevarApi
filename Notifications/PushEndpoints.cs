using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Web push: the public key browsers subscribe with, and signed-in users turning notifications on or off for a device
/// (staff hear about new records, customers about their bookings). Anyone can also subscribe as part of a kitchen booking (<see cref="KitchenBookingRequest.Push"/>).
/// </summary>
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/push/public-key", GetPublicKey)
            .WithTags("Push")
            .WithName("GetPushPublicKey")
            .WithSummary("The VAPID public key browsers subscribe with (404 while push is off)");

        var device = endpoints.MapGroup("/api/push/subscription")
            .WithTags("Push")
            .RequireAuthorization();
        device.MapPut("/", SubscribeAsync)
            .WithName("SubscribeDevice")
            .WithSummary("Send the signed-in user's notifications to this device");
        device.MapDelete("/", UnsubscribeAsync)
            .WithName("UnsubscribeDevice")
            .WithSummary("Stop sending notifications to this device");

        return endpoints;
    }

    private static Results<Ok<PushPublicKeyResponse>, NotFound> GetPublicKey(IOptions<PushOptions> options) =>
        options.Value.IsConfigured
            ? TypedResults.Ok(new PushPublicKeyResponse(options.Value.PublicKey!))
            : TypedResults.NotFound();

    private static async Task<Results<NoContent, ValidationProblem>> SubscribeAsync(
        PushSubscriptionRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!request.HasKnownPushService())
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["endpoint"] = ["This isn't a browser push service address."]
            });
        }

        var userId = userManager.GetUserId(principal)!;
        var existing = await db.WebPushSubscriptions
            .FirstOrDefaultAsync(subscription => subscription.UserId == userId && subscription.Endpoint == request.Endpoint, cancellationToken);
        if (existing is null)
        {
            db.WebPushSubscriptions.Add(new WebPushSubscription
            {
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                UserId = userId
            });
        }
        else
        {
            existing.P256dh = request.P256dh;
            existing.Auth = request.Auth;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UnsubscribeAsync(
        string endpoint,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(principal)!;
        await db.WebPushSubscriptions
            // Also its bookings: off means off. Only this device knows its endpoint, so this can't silence another's.
            .Where(subscription => subscription.Endpoint == endpoint && (subscription.UserId == userId || subscription.KitchenBookingId != null))
            .ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

public sealed record PushPublicKeyResponse(string PublicKey);
