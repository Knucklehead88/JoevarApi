using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

/// <summary>
/// The Kitchen Design page's "Schedule your free blueprint": anyone can book an on-site design visit with their
/// chosen options. The booking is saved as Pending, then emailed to the office (the customer as Reply-To); staff
/// approve or decline it (<see cref="KitchenBookingStaffEndpoints"/>).
/// </summary>
public static class KitchenBookingEndpoints
{
    /// <summary>The visit times offered (Joevar's local time), as on the old site.</summary>
    public static readonly string[] TimeSlots = ["09:00", "10:00", "11:00", "13:00", "14:00", "15:00", "16:00"];

    /// <summary>Visits are booked at least this many days ahead.</summary>
    public const int LeadDays = 5;

    // Joevar is in Forest Park, Illinois: "today" is Chicago's date, not the server's.
    private static readonly TimeZoneInfo LocalTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    /// <summary>Today's date in Joevar's time zone.</summary>
    public static DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LocalTimeZone).DateTime);

    /// <summary>A slot such as "13:00" as people write it: "1:00 PM".</summary>
    public static string DisplayTime(string slot) => TimeOnly.ParseExact(slot, "HH:mm").ToString("h:mm tt");

    /// <summary>The selections as an indented email list, or <paramref name="none"/>.</summary>
    public static string SelectionList(KitchenBooking booking, string none) =>
        booking.Selections.Count > 0
            ? string.Join(Environment.NewLine, booking.Selections.Select(selection => $"  - {selection}"))
            : $"  {none}";

    public static IEndpointRouteBuilder MapKitchenBookingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/kitchen-bookings", CreateBookingAsync)
            .WithTags("Kitchen")
            .RequireRateLimiting(ContactEndpoints.RateLimitPolicy)
            .WithName("CreateKitchenBooking")
            .WithSummary("Book a free on-site kitchen design visit with the chosen options");

        return endpoints;
    }

    private static async Task<Results<NoContent, ValidationProblem>> CreateBookingAsync(
        KitchenBookingRequest request,
        AppDbContext db,
        OfficeEmailSender emailSender,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            errors["phone"] = ["Phone must be a valid phone number, e.g. +17735550100."];
        }

        var earliest = Today().AddDays(LeadDays);
        if (request.Date < earliest)
        {
            errors["date"] = [$"Choose a date at least {LeadDays} days ahead ({earliest:MMMM d} or later)."];
        }

        if (!TimeSlots.Contains(request.Time))
        {
            errors["time"] = [$"Choose one of: {string.Join(", ", TimeSlots)}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var booking = new KitchenBooking
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            Phone = phone!,
            AppointmentDate = request.Date,
            AppointmentTime = request.Time,
            Selections = request.Selections.Select(selection => $"{selection.Category.Trim()}: {selection.Option.Trim()}").ToList(),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        };
        db.KitchenBookings.Add(booking);
        if (request.Push is { } push && push.HasKnownPushService())
        {
            db.WebPushSubscriptions.Add(new WebPushSubscription
            {
                Endpoint = push.Endpoint,
                P256dh = push.P256dh,
                Auth = push.Auth,
                KitchenBookingId = booking.Id
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var logger = loggerFactory.CreateLogger("KitchenBooking");
        logger.LogInformation("A kitchen design booking was received.");

        // Saved first, so a mail problem never loses a booking or shows the customer an error.
        try
        {
            var time = DisplayTime(booking.AppointmentTime);
            var selections = SelectionList(booking, "(none chosen)");
            await emailSender.SendAsync(
                $"Kitchen design booking: {booking.Name}, {booking.AppointmentDate:ddd MMM d} at {time}",
                $"""
                New "Schedule your free blueprint" booking from the Kitchen Design page.

                Visit: {booking.AppointmentDate:dddd, MMMM d, yyyy} at {time}

                Name:  {booking.Name}
                Email: {booking.Email}
                Phone: {booking.Phone}

                Selections:
                {selections}

                Notes:
                {booking.Notes ?? "-"}

                Approve or decline it in the staff Calendar. Reply to this email to answer {booking.Name}.
                """,
                booking.Name,
                booking.Email,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Kitchen booking {Id} was saved but could not be emailed.", booking.Id);
        }

        // The customer's copy: what they asked for, and that the office will confirm the visit.
        try
        {
            await emailSender.SendToCustomerAsync(
                booking.Name,
                booking.Email,
                "We received your kitchen design request",
                CustomerEmail(booking),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Kitchen booking {Id} was saved but the customer's email could not be sent.", booking.Id);
        }

        return TypedResults.NoContent();
    }

    private static string CustomerEmail(KitchenBooking booking)
    {
        var time = DisplayTime(booking.AppointmentTime);
        var selections = SelectionList(booking, "(none chosen yet; we'll go through the options with you at the visit)");
        return $"""
            Hi {booking.Name},

            Thank you for your kitchen design request. We've received it and will contact you shortly to confirm
            your appointment.

            Requested visit: {booking.AppointmentDate:dddd, MMMM d, yyyy} at {time}

            Your selections:
            {selections}

            Your notes:
            {booking.Notes ?? "-"}

            This is not yet a confirmation of your appointment; you'll receive one from us once it's scheduled.
            To change anything, just reply to this email.

            Joevar
            """;
    }
}

public sealed record KitchenBookingRequest
{
    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }

    /// <summary>The visit date, e.g. "2026-11-03".</summary>
    public required DateOnly Date { get; init; }

    /// <summary>One of the offered slots, e.g. "09:00".</summary>
    [Required]
    [MaxLength(5)]
    public required string Time { get; init; }

    [MaxLength(20)]
    public List<KitchenSelection> Selections { get; init; } = [];

    [MaxLength(4000)]
    public string? Notes { get; init; }

    /// <summary>
    /// This device's push subscription, when the customer asked to be notified of the decision on this booking. An
    /// unusable one is ignored rather than refusing the booking.
    /// </summary>
    public PushSubscriptionRequest? Push { get; init; }
}

public sealed record KitchenSelection
{
    /// <summary>The slider, e.g. "Quartz Countertops".</summary>
    [Required]
    [MaxLength(100)]
    public required string Category { get; init; }

    /// <summary>The chosen option's name.</summary>
    [Required]
    [MaxLength(200)]
    public required string Option { get; init; }
}
