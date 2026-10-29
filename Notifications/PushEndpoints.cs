using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Web push: the public key browsers subscribe with, and staff devices turning the staff alerts on or off. Customers
/// subscribe as part of their kitchen booking (<see cref="KitchenBookingRequest.Push"/>).
/// </summary>
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/push/public-key", GetPublicKey)
            .WithTags("Push")
            .WithName("GetPushPublicKey")
            .WithSummary("The VAPID public key browsers subscribe with (404 while push is off)");

        var staff = endpoints.MapGroup("/api/push/staff-subscription")
            .WithTags("Push")
            .RequireAuthorization(AuthPolicies.Staff);
        staff.MapPut("/", SubscribeStaffAsync)
            .WithName("SubscribeStaffDevice")
            .WithSummary("Send the staff alerts (new bookings, registrations, customers) to this device");
        staff.MapDelete("/", UnsubscribeStaffAsync)
            .WithName("UnsubscribeStaffDevice")
            .WithSummary("Stop sending the staff alerts to this device");

        return endpoints;
    }

    private static Results<Ok<PushPublicKeyResponse>, NotFound> GetPublicKey(IOptions<PushOptions> options) =>
        options.Value.IsConfigured
            ? TypedResults.Ok(new PushPublicKeyResponse(options.Value.PublicKey!))
            : TypedResults.NotFound();

    private static async Task<Results<NoContent, ValidationProblem>> SubscribeStaffAsync(
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

    private static async Task<NoContent> UnsubscribeStaffAsync(
        string endpoint,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(principal)!;
        await db.WebPushSubscriptions
            .Where(subscription => subscription.UserId == userId && subscription.Endpoint == endpoint)
            .ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

public sealed record PushPublicKeyResponse(string PublicKey);
