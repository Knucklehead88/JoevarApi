/// <summary>A "Schedule your free blueprint" booking from the Kitchen Design page.</summary>
public sealed class KitchenBooking
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    /// <summary>E.164.</summary>
    public required string Phone { get; set; }

    /// <summary>Where the visit is: street and number, e.g. "1114 Circle Ave". Empty on bookings from before 2026-11-03.</summary>
    public string StreetAddress { get; set; } = "";

    public string City { get; set; } = "";

    /// <summary>A US state's full name, e.g. "Illinois".</summary>
    public string State { get; set; } = "";

    /// <summary>"60130" or "60130-1234".</summary>
    public string ZipCode { get; set; } = "";

    /// <summary>The address on one line, for emails.</summary>
    public string AddressLine => string.IsNullOrEmpty(StreetAddress) ? "-" : $"{StreetAddress}, {City}, {State} {ZipCode}";

    /// <summary>
    /// The on-site design visit (Joevar's local date and time): what the customer asked for, or the slot staff moved
    /// it to when approving.
    /// </summary>
    public DateOnly AppointmentDate { get; set; }

    /// <summary>One of <see cref="KitchenBookingEndpoints.TimeSlots"/>, e.g. "09:00".</summary>
    public required string AppointmentTime { get; set; }

    /// <summary>The chosen options as "Category: Option", e.g. "Quartz Countertops: Calacatta Alto".</summary>
    public List<string> Selections { get; set; } = [];

    public string? Notes { get; set; }

    public KitchenBookingStatus Status { get; set; } = KitchenBookingStatus.Pending;

    /// <summary>When staff approved or declined it.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The user name of the staff member who approved or declined it.</summary>
    public string? DecidedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The customer record the booking belongs to (found by email or phone, else created when it's paid for).</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>The deposit asked for, in cents; null when no deposit was required (then it went straight to Pending).</summary>
    public long? DepositCents { get; set; }

    /// <summary>The Stripe Checkout session the customer pays in.</summary>
    public string? StripeCheckoutSessionId { get; set; }

    /// <summary>The Stripe payment, once paid (refunds go against it).</summary>
    public string? StripePaymentIntentId { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>When the deposit was refunded (declining a paid booking refunds it).</summary>
    public DateTimeOffset? RefundedAt { get; set; }
}

/// <summary>
/// New bookings wait for staff, who approve (confirming the visit) or decline them. With a deposit, a booking is
/// AwaitingPayment until Stripe says it's paid (then Pending), or Expired.
/// </summary>
public enum KitchenBookingStatus
{
    Pending,
    Approved,
    Declined,

    /// <summary>Saved, but the deposit isn't paid yet: staff don't see it until it is.</summary>
    AwaitingPayment,

    /// <summary>The deposit was never paid (the Stripe checkout expired or was abandoned).</summary>
    Expired
}
