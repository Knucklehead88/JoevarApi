using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

/// <summary>Stripe settings: the secret key ("sk_test_…"/"sk_live_…") and the webhook endpoint's signing secret ("whsec_…").</summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public string? SecretKey { get; init; }

    public string? WebhookSecret { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// Card payments through Stripe's hosted pages. The kitchen design visit's deposit: a booking is saved as
/// AwaitingPayment and the customer is sent to Stripe Checkout; when Stripe says it's paid (the webhook, or asking
/// Stripe when they come back, whichever is first; <see cref="FinalizePaidAsync"/> runs once) it becomes Pending, the
/// customer's "Paid Deposit" stage is completed, and the office and customer are emailed. Declining a paid booking
/// refunds it. Also the customers' saved cards (Checkout in setup mode), shown on their profile.
/// </summary>
public sealed class KitchenPayments(
    AppDbContext db,
    IOptions<StripeOptions> options,
    IConfiguration configuration,
    OfficeEmailSender emailSender,
    PushQueue pushQueue,
    ILogger<KitchenPayments> logger)
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public bool IsConfigured => options.Value.IsConfigured;

    private StripeClient Client => new(options.Value.SecretKey);

    public static string Money(long cents) => (cents / 100m).ToString(cents % 100 == 0 ? "C0" : "C2", Us);

    /// <summary>The deposit set in the staff app, in cents (0: none).</summary>
    public static async Task<long> StoredDepositCentsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var value = await db.SiteSettings.Where(setting => setting.Key == SiteSetting.KitchenDepositCents)
            .Select(setting => setting.Value).FirstOrDefaultAsync(cancellationToken);
        return long.TryParse(value, out var cents) && cents > 0 ? cents : 0;
    }

    /// <summary>The deposit a new booking pays: the stored one, or 0 while Stripe isn't set up.</summary>
    public async Task<long> DepositCentsAsync(CancellationToken cancellationToken) =>
        IsConfigured ? await StoredDepositCentsAsync(db, cancellationToken) : 0;

    /// <summary>
    /// Where Stripe sends the customer back: the site they came from when it's one of ours (CORS origins), else the
    /// first of them. Never an address from the request alone.
    /// </summary>
    public string ReturnOrigin(HttpContext httpContext)
    {
        var allowed = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var origin = httpContext.Request.Headers.Origin.ToString();
        return allowed.Contains(origin) ? origin : allowed.FirstOrDefault() ?? "https://joevar.com";
    }

    /// <summary>Creates the Stripe Checkout page for a saved AwaitingPayment booking and returns its address.</summary>
    public async Task<string> StartCheckoutAsync(KitchenBooking booking, string origin, CancellationToken cancellationToken)
    {
        var customer = await EnsureCustomerAsync(booking.Name, booking.Email, booking.Phone, cancellationToken);
        booking.CustomerId = customer.Id;
        var when = $"{booking.AppointmentDate:dddd, MMMM d} at {KitchenBookingEndpoints.DisplayTime(booking.AppointmentTime)}";
        var metadata = new Dictionary<string, string> { ["kitchenBookingId"] = booking.Id.ToString() };
        var session = await new SessionService(Client).CreateAsync(new SessionCreateOptions
        {
            Mode = "payment",
            Customer = customer.StripeCustomerId,
            ClientReferenceId = booking.Id.ToString(),
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = booking.DepositCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = "Kitchen design visit deposit",
                            Description = $"Free blueprint visit, {when}"
                        }
                    }
                }
            ],
            // Keeps the card on their Stripe customer, so it shows on their profile and can be used again.
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                SetupFutureUsage = "off_session",
                ReceiptEmail = booking.Email,
                Description = $"Joevar kitchen design visit deposit ({when})",
                Metadata = metadata
            },
            Metadata = metadata,
            SuccessUrl = $"{origin}/kitchen-design?booking={booking.Id}",
            CancelUrl = $"{origin}/kitchen-design?booking={booking.Id}&cancelled=1",
            // Stripe's shortest; an unpaid booking expires with it (webhook, or the next status check).
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        }, cancellationToken: cancellationToken);

        booking.StripeCheckoutSessionId = session.Id;
        await db.SaveChangesAsync(cancellationToken);
        return session.Url;
    }

    /// <summary>
    /// A booking's payment as it stands, asking Stripe when it's still AwaitingPayment (so a customer coming back from
    /// Checkout sees it as paid even before the webhook arrives, and it works without a webhook locally).
    /// </summary>
    public async Task<KitchenBooking?> SyncAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.KitchenBookings.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == bookingId, cancellationToken);
        if (booking is { Status: KitchenBookingStatus.AwaitingPayment, StripeCheckoutSessionId: { } sessionId } && IsConfigured)
        {
            var session = await new SessionService(Client).GetAsync(sessionId, cancellationToken: cancellationToken);
            await ApplySessionAsync(session, cancellationToken);
            booking = await db.KitchenBookings.AsNoTracking().FirstAsync(candidate => candidate.Id == bookingId, cancellationToken);
        }

        return booking;
    }

    /// <summary>A Checkout session from the webhook or a status check: paid → Pending, expired → Expired.</summary>
    public async Task ApplySessionAsync(Session session, CancellationToken cancellationToken)
    {
        if (session.Mode != "payment" || !Guid.TryParse(session.ClientReferenceId, out var bookingId))
        {
            return;
        }

        if (session.PaymentStatus == "paid")
        {
            await FinalizePaidAsync(bookingId, session.PaymentIntentId, cancellationToken);
        }
        else if (session.Status == "expired")
        {
            await db.KitchenBookings
                .Where(booking => booking.Id == bookingId && booking.Status == KitchenBookingStatus.AwaitingPayment)
                .ExecuteUpdateAsync(setters => setters.SetProperty(booking => booking.Status, KitchenBookingStatus.Expired), cancellationToken);
        }
    }

    /// <summary>
    /// The deposit is paid: the booking goes to the office (Pending), the customer's "Paid Deposit" stage is done, and the
    /// office and customer are emailed. A conditional update makes it happen once, even when the webhook and the
    /// customer's return arrive together.
    /// </summary>
    private async Task FinalizePaidAsync(Guid bookingId, string paymentIntentId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = await db.KitchenBookings
            .Where(booking => booking.Id == bookingId && (booking.Status == KitchenBookingStatus.AwaitingPayment || booking.Status == KitchenBookingStatus.Expired))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(booking => booking.Status, KitchenBookingStatus.Pending)
                .SetProperty(booking => booking.PaidAt, now)
                .SetProperty(booking => booking.StripePaymentIntentId, paymentIntentId), cancellationToken);
        if (changed == 0)
        {
            return;
        }

        var booking = await db.KitchenBookings.AsNoTracking().FirstAsync(candidate => candidate.Id == bookingId, cancellationToken);
        logger.LogInformation("Kitchen booking {Id}: deposit paid.", booking.Id);
        if (booking.CustomerId is { } customerId)
        {
            await CompleteDepositStageAsync(customerId, now, cancellationToken);
        }

        pushQueue.Enqueue(
            new PushAudience.Staff(),
            new PushPayload(
                "New kitchen design request (deposit paid)",
                $"{booking.Name}: {booking.AppointmentDate:ddd, MMM d} at {KitchenBookingEndpoints.DisplayTime(booking.AppointmentTime)}",
                "/calendar",
                "new-booking"));
        await KitchenBookingEndpoints.SendReceivedEmailsAsync(booking, emailSender, logger, cancellationToken);
    }

    private async Task CompleteDepositStageAsync(Guid customerId, DateTimeOffset when, CancellationToken cancellationToken)
    {
        var stageId = await db.CustomerStages.Where(stage => stage.Key == CustomerStage.DepositKey).Select(stage => (Guid?)stage.Id).FirstOrDefaultAsync(cancellationToken);
        if (stageId is null || await db.CustomerStageCompletions.AnyAsync(done => done.CustomerId == customerId && done.StageId == stageId, cancellationToken))
        {
            return;
        }

        db.CustomerStageCompletions.Add(new CustomerStageCompletion { CustomerId = customerId, StageId = stageId.Value, CompletedAt = when, CompletedBy = "Stripe" });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Refunds a paid, not yet refunded deposit in full. Returns whether a refund was made.</summary>
    public async Task<bool> RefundAsync(KitchenBooking booking, CancellationToken cancellationToken)
    {
        if (!IsConfigured || booking.StripePaymentIntentId is null || booking.RefundedAt is not null)
        {
            return false;
        }

        await new RefundService(Client).CreateAsync(
            new RefundCreateOptions { PaymentIntent = booking.StripePaymentIntentId, Reason = "requested_by_customer" },
            cancellationToken: cancellationToken);
        booking.RefundedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>The customer record for these details (by phone, then email), with a Stripe customer; created when new.</summary>
    public async Task<Customer> EnsureCustomerAsync(string name, string email, string? phone, CancellationToken cancellationToken)
    {
        var customer = phone is null ? null : await db.Customers.Include(candidate => candidate.Phone)
            .Where(candidate => candidate.Phone != null && candidate.Phone.Number == phone)
            .OrderBy(candidate => candidate.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        customer ??= await db.Customers.Include(candidate => candidate.Phone)
            .Where(candidate => candidate.Email != null && EF.Functions.ILike(candidate.Email, email))
            .OrderBy(candidate => candidate.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            customer = new Customer { Name = name, Email = email, Phone = phone is null ? null : new ContactPhone { Number = phone } };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(cancellationToken);
        }

        await EnsureStripeCustomerAsync(customer, cancellationToken);
        return customer;
    }

    public async Task EnsureStripeCustomerAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (customer.StripeCustomerId is not null)
        {
            return;
        }

        var stripeCustomer = await new CustomerService(Client).CreateAsync(new CustomerCreateOptions
        {
            Name = customer.Name,
            Email = customer.Email,
            Phone = customer.Phone?.Number,
            Metadata = new Dictionary<string, string> { ["joevarCustomerId"] = customer.Id.ToString() }
        }, cancellationToken: cancellationToken);
        customer.StripeCustomerId = stripeCustomer.Id;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The customer's saved cards, newest first.</summary>
    public async Task<List<SavedCard>> ListCardsAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (customer.StripeCustomerId is null)
        {
            return [];
        }

        var methods = await new CustomerPaymentMethodService(Client).ListAsync(
            customer.StripeCustomerId,
            new CustomerPaymentMethodListOptions { Type = "card", Limit = 20 },
            cancellationToken: cancellationToken);
        return methods.Data
            .Where(method => method.Card is not null)
            .Select(method => new SavedCard(method.Id, method.Card.Brand, method.Card.Last4, (int)method.Card.ExpMonth, (int)method.Card.ExpYear))
            .ToList();
    }

    /// <summary>Stripe's page to add a card (Checkout in setup mode); it returns to the profile page.</summary>
    public async Task<string> StartCardSetupAsync(Customer customer, string origin, CancellationToken cancellationToken)
    {
        await EnsureStripeCustomerAsync(customer, cancellationToken);
        var session = await new SessionService(Client).CreateAsync(new SessionCreateOptions
        {
            Mode = "setup",
            Currency = "usd",
            Customer = customer.StripeCustomerId,
            SuccessUrl = $"{origin}/profile?card=added",
            CancelUrl = $"{origin}/profile"
        }, cancellationToken: cancellationToken);
        return session.Url;
    }

    /// <summary>Removes a saved card, only when it's this customer's. Returns false otherwise.</summary>
    public async Task<bool> RemoveCardAsync(Customer customer, string paymentMethodId, CancellationToken cancellationToken)
    {
        if (customer.StripeCustomerId is null)
        {
            return false;
        }

        var service = new PaymentMethodService(Client);
        PaymentMethod method;
        try
        {
            method = await service.GetAsync(paymentMethodId, cancellationToken: cancellationToken);
        }
        catch (StripeException)
        {
            return false;
        }

        if (method.CustomerId != customer.StripeCustomerId)
        {
            return false;
        }

        await service.DetachAsync(paymentMethodId, cancellationToken: cancellationToken);
        return true;
    }
}

/// <summary>A card saved in Stripe: brand ("visa"), last 4 digits and expiry.</summary>
public sealed record SavedCard(string Id, string Brand, string Last4, int ExpMonth, int ExpYear);
