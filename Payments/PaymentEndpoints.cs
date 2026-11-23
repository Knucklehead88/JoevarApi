using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

/// <summary>
/// Payments (<see cref="KitchenPayments"/>): Stripe's webhook, the deposit status a customer sees when coming back from
/// Stripe, the deposit amount (public, and the staff setting), and the signed-in customer's saved cards.
/// </summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/stripe/webhook", HandleWebhookAsync)
            .WithTags("Payments")
            .WithName("StripeWebhook")
            .WithSummary("Stripe events (signed): a paid or expired deposit checkout")
            .ExcludeFromDescription();

        endpoints.MapGet("/api/kitchen-bookings/deposit", GetDepositAsync)
            .WithTags("Payments")
            .WithName("GetKitchenDeposit")
            .WithSummary("The deposit a kitchen design visit takes, in cents (0: none)");

        endpoints.MapGet("/api/kitchen-bookings/{id:guid}/payment", GetBookingPaymentAsync)
            .WithTags("Payments")
            .WithName("GetKitchenBookingPayment")
            .WithSummary("Whether a booking's deposit is paid (asks Stripe while it's waiting)");

        var settings = endpoints.MapGroup("/api/settings").WithTags("Payments").RequireAuthorization(AuthPolicies.Staff);
        settings.MapGet("/booking", GetBookingSettingsAsync).WithName("GetBookingSettings").WithSummary("The kitchen visit deposit and whether Stripe is set up");
        settings.MapPut("/booking", UpdateBookingSettingsAsync).WithName("UpdateBookingSettings").WithSummary("Set the kitchen visit deposit");

        var cards = endpoints.MapGroup("/api/me/cards").WithTags("Payments").RequireAuthorization();
        cards.MapGet("/", ListCardsAsync).WithName("ListMyCards").WithSummary("Your saved cards");
        cards.MapPost("/setup", StartCardSetupAsync).WithName("StartCardSetup").WithSummary("Stripe's page to add a card");
        cards.MapDelete("/{id}", RemoveCardAsync).WithName("RemoveMyCard").WithSummary("Remove a saved card");

        return endpoints;
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpRequest request,
        IOptions<StripeOptions> options,
        KitchenPayments payments,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("StripeWebhook");
        if (string.IsNullOrWhiteSpace(options.Value.WebhookSecret) || !payments.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Stripe isn't set up.");
        }

        var json = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json, request.Headers["Stripe-Signature"], options.Value.WebhookSecret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException)
        {
            logger.LogWarning("Rejected a Stripe webhook with an invalid signature.");
            return Results.BadRequest();
        }

        if (stripeEvent.Type is EventTypes.CheckoutSessionCompleted or EventTypes.CheckoutSessionAsyncPaymentSucceeded or EventTypes.CheckoutSessionExpired
            && stripeEvent.Data.Object is Session session)
        {
            await payments.ApplySessionAsync(session, cancellationToken);
        }

        return Results.Ok();
    }

    private static async Task<Ok<DepositResponse>> GetDepositAsync(KitchenPayments payments, CancellationToken cancellationToken) =>
        TypedResults.Ok(new DepositResponse(await payments.DepositCentsAsync(cancellationToken)));

    /// <summary>The id is the booking's own (a GUID only its customer has, from Stripe's return address).</summary>
    private static async Task<Results<Ok<BookingPaymentResponse>, NotFound>> GetBookingPaymentAsync(
        Guid id,
        KitchenPayments payments,
        CancellationToken cancellationToken)
    {
        var booking = await payments.SyncAsync(id, cancellationToken);
        if (booking is null)
        {
            return TypedResults.NotFound();
        }

        var paid = booking.PaidAt is not null;
        return TypedResults.Ok(new BookingPaymentResponse(
            paid ? "Paid" : booking.Status == KitchenBookingStatus.AwaitingPayment ? "Waiting" : "Unpaid",
            booking.DepositCents,
            booking.AppointmentDate,
            booking.AppointmentTime,
            booking.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? booking.Name));
    }

    private static async Task<Ok<BookingSettingsResponse>> GetBookingSettingsAsync(
        AppDbContext db,
        KitchenPayments payments,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(new BookingSettingsResponse(await KitchenPayments.StoredDepositCentsAsync(db, cancellationToken), payments.IsConfigured));

    private static async Task<Ok<BookingSettingsResponse>> UpdateBookingSettingsAsync(
        BookingSettingsRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        KitchenPayments payments,
        CancellationToken cancellationToken)
    {
        var setting = await db.SiteSettings.FirstOrDefaultAsync(candidate => candidate.Key == SiteSetting.KitchenDepositCents, cancellationToken);
        if (setting is null)
        {
            setting = new SiteSetting { Key = SiteSetting.KitchenDepositCents, Value = "0" };
            db.SiteSettings.Add(setting);
        }

        setting.Value = request.DepositCents.ToString(System.Globalization.CultureInfo.InvariantCulture);
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        setting.UpdatedBy = principal.Identity?.Name;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new BookingSettingsResponse(request.DepositCents, payments.IsConfigured));
    }

    private static async Task<Results<Ok<CardsResponse>, ProblemHttpResult>> ListCardsAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        KitchenPayments payments,
        CancellationToken cancellationToken)
    {
        if (!payments.IsConfigured)
        {
            return TypedResults.Ok(new CardsResponse(false, []));
        }

        var customer = await CustomerOfAsync(principal, userManager, db, cancellationToken);
        if (customer is null)
        {
            return TypedResults.Ok(new CardsResponse(false, []));
        }

        try
        {
            return TypedResults.Ok(new CardsResponse(true, await payments.ListCardsAsync(customer, cancellationToken)));
        }
        catch (StripeException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Your cards couldn't be loaded.");
        }
    }

    private static async Task<Results<Ok<CardSetupResponse>, NotFound, ProblemHttpResult>> StartCardSetupAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        KitchenPayments payments,
        CancellationToken cancellationToken)
    {
        var customer = payments.IsConfigured ? await CustomerOfAsync(principal, userManager, db, cancellationToken) : null;
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            return TypedResults.Ok(new CardSetupResponse(await payments.StartCardSetupAsync(customer, payments.ReturnOrigin(httpContext), cancellationToken)));
        }
        catch (StripeException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Stripe's page couldn't be opened.");
        }
    }

    private static async Task<Results<NoContent, NotFound>> RemoveCardAsync(
        string id,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        KitchenPayments payments,
        CancellationToken cancellationToken)
    {
        var customer = payments.IsConfigured ? await CustomerOfAsync(principal, userManager, db, cancellationToken) : null;
        return customer is not null && await payments.RemoveCardAsync(customer, id, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    /// <summary>The customer record a customer's login belongs to (staff and contractors have none).</summary>
    private static async Task<Customer?> CustomerOfAsync(
        ClaimsPrincipal principal, UserManager<AppUser> userManager, AppDbContext db, CancellationToken cancellationToken)
    {
        var customerId = (await userManager.GetUserAsync(principal))?.CustomerId;
        return customerId is null
            ? null
            : await db.Customers.Include(customer => customer.Phone).FirstOrDefaultAsync(customer => customer.Id == customerId, cancellationToken);
    }
}

/// <summary>The kitchen visit deposit in cents; 0 when there's none (or Stripe isn't set up).</summary>
public sealed record DepositResponse(long DepositCents);

/// <summary><see cref="Status"/>: Paid, Waiting (not paid yet, checkout still open) or Unpaid (expired or cancelled).</summary>
public sealed record BookingPaymentResponse(string Status, long? DepositCents, DateOnly Date, string Time, string FirstName);

public sealed record BookingSettingsResponse(long DepositCents, bool PaymentsConfigured);

public sealed record BookingSettingsRequest
{
    /// <summary>0 turns the deposit off; at most $10,000.</summary>
    [Range(0, 1_000_000, ErrorMessage = "Enter an amount from $0 to $10,000.")]
    public long DepositCents { get; init; }
}

/// <summary><see cref="Available"/>: payments are set up and this login is a customer's.</summary>
public sealed record CardsResponse(bool Available, List<SavedCard> Cards);

public sealed record CardSetupResponse(string Url);
