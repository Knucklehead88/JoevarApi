using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The staff Calendar: list kitchen design bookings, and approve (optionally moving the visit) or decline the pending
/// ones. Both decisions email the customer, after saving, so a mail problem never undoes a decision.
/// </summary>
public static class KitchenBookingStaffEndpoints
{
    public static IEndpointRouteBuilder MapKitchenBookingStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/kitchen-bookings")
            .WithTags("Kitchen")
            .RequireAuthorization(AuthPolicies.Staff);

        group.MapGet("/", ListBookingsAsync).WithName("ListKitchenBookings").WithSummary("List kitchen design bookings");
        group.MapPost("/{id:guid}/approve", ApproveBookingAsync)
            .WithName("ApproveKitchenBooking")
            .WithSummary("Approve a pending booking, optionally at another date or time, and email the customer");
        group.MapPost("/{id:guid}/decline", DeclineBookingAsync)
            .WithName("DeclineKitchenBooking")
            .WithSummary("Decline a pending booking and email the customer");

        return endpoints;
    }

    /// <summary>Bookings by visit date and time; <paramref name="from"/> and <paramref name="to"/> are inclusive.</summary>
    private static async Task<Ok<PagedResult<KitchenBookingResponse>>> ListBookingsAsync(
        AppDbContext db,
        KitchenBookingStatus? status,
        DateOnly? from,
        DateOnly? to,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.KitchenBookings.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(booking => booking.Status == status.Value);
        }

        if (from is not null)
        {
            query = query.Where(booking => booking.AppointmentDate >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(booking => booking.AppointmentDate <= to.Value);
        }

        var result = await query
            .OrderBy(booking => booking.AppointmentDate)
            .ThenBy(booking => booking.AppointmentTime)
            .Select(ToResponse)
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<KitchenBookingResponse>, NotFound, Conflict<string>, ValidationProblem>> ApproveBookingAsync(
        Guid id,
        ApproveKitchenBookingRequest request,
        AppDbContext db,
        OfficeEmailSender emailSender,
        PushQueue pushQueue,
        ClaimsPrincipal principal,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var booking = await db.KitchenBookings.FirstOrDefaultAsync(booking => booking.Id == id, cancellationToken);
        if (booking is null)
        {
            return TypedResults.NotFound();
        }

        if (booking.Status != KitchenBookingStatus.Pending)
        {
            return TypedResults.Conflict($"This booking was already {booking.Status.ToString().ToLowerInvariant()}.");
        }

        // Staff may move the visit; the customer's 5-day lead time doesn't apply to them, but the past does.
        var date = request.Date ?? booking.AppointmentDate;
        var time = request.Time ?? booking.AppointmentTime;
        var errors = new Dictionary<string, string[]>();
        if (date < KitchenBookingEndpoints.Today())
        {
            errors["date"] = ["Choose today or a later date."];
        }

        if (!KitchenBookingEndpoints.TimeSlots.Contains(time))
        {
            errors["time"] = [$"Choose one of: {string.Join(", ", KitchenBookingEndpoints.TimeSlots)}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var requested = (booking.AppointmentDate, booking.AppointmentTime);
        booking.AppointmentDate = date;
        booking.AppointmentTime = time;
        booking.Status = KitchenBookingStatus.Approved;
        booking.DecidedAt = DateTimeOffset.UtcNow;
        booking.DecidedBy = principal.Identity?.Name;
        await db.SaveChangesAsync(cancellationToken);

        var when = $"{booking.AppointmentDate:ddd, MMM d} at {KitchenBookingEndpoints.DisplayTime(booking.AppointmentTime)}";
        pushQueue.Enqueue(
            new PushAudience.Booking(booking.Id),
            requested != (date, time)
                ? new PushPayload("Your visit was rescheduled", $"Your kitchen design visit is now {when}. Details are in your email.", "/", "booking-decision")
                : new PushPayload("Your visit is confirmed", $"Your kitchen design visit is confirmed for {when}.", "/", "booking-decision"));

        var moved = requested != (date, time)
            ? $"""

              We've moved it from the time you asked for ({requested.AppointmentDate:dddd, MMMM d} at {KitchenBookingEndpoints.DisplayTime(requested.AppointmentTime)}).
              If the new time doesn't suit you, just reply to this email.

              """
            : Environment.NewLine;
        await EmailCustomerAsync(
            emailSender,
            loggerFactory,
            booking,
            "Your kitchen design visit is confirmed",
            $"""
            Hi {booking.Name},

            Your free kitchen design visit is confirmed:

            {booking.AppointmentDate:dddd, MMMM d, yyyy} at {KitchenBookingEndpoints.DisplayTime(booking.AppointmentTime)}
            {moved}
            Your selections:
            {KitchenBookingEndpoints.SelectionList(booking, "(none chosen yet; we'll go through the options with you at the visit)")}

            To change anything, just reply to this email.

            Joevar
            """,
            cancellationToken);

        return TypedResults.Ok(ToResponse.Compile()(booking));
    }

    private static async Task<Results<Ok<KitchenBookingResponse>, NotFound, Conflict<string>>> DeclineBookingAsync(
        Guid id,
        DeclineKitchenBookingRequest request,
        AppDbContext db,
        OfficeEmailSender emailSender,
        PushQueue pushQueue,
        ClaimsPrincipal principal,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var booking = await db.KitchenBookings.FirstOrDefaultAsync(booking => booking.Id == id, cancellationToken);
        if (booking is null)
        {
            return TypedResults.NotFound();
        }

        if (booking.Status != KitchenBookingStatus.Pending)
        {
            return TypedResults.Conflict($"This booking was already {booking.Status.ToString().ToLowerInvariant()}.");
        }

        booking.Status = KitchenBookingStatus.Declined;
        booking.DecidedAt = DateTimeOffset.UtcNow;
        booking.DecidedBy = principal.Identity?.Name;
        await db.SaveChangesAsync(cancellationToken);

        pushQueue.Enqueue(
            new PushAudience.Booking(booking.Id),
            new PushPayload(
                "About your kitchen design request",
                "We can't schedule your visit at the time you asked for. Details are in your email.",
                "/",
                "booking-decision"));

        var message = string.IsNullOrWhiteSpace(request.Message)
            ? Environment.NewLine
            : $"""

              {request.Message.Trim()}

              """;
        await EmailCustomerAsync(
            emailSender,
            loggerFactory,
            booking,
            "About your kitchen design request",
            $"""
            Hi {booking.Name},

            Thank you for your kitchen design request. Unfortunately we can't schedule your visit on
            {booking.AppointmentDate:dddd, MMMM d} at {KitchenBookingEndpoints.DisplayTime(booking.AppointmentTime)}.
            {message}
            Reply to this email and we'll find a time that works, or book another time on our Kitchen Design page.

            Joevar
            """,
            cancellationToken);

        return TypedResults.Ok(ToResponse.Compile()(booking));
    }

    private static async Task EmailCustomerAsync(
        OfficeEmailSender emailSender,
        ILoggerFactory loggerFactory,
        KitchenBooking booking,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendToCustomerAsync(booking.Name, booking.Email, subject, body, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("KitchenBooking").LogError(
                exception, "Kitchen booking {Id} was {Status} but the customer's email could not be sent.", booking.Id, booking.Status);
        }
    }

    private static readonly Expression<Func<KitchenBooking, KitchenBookingResponse>> ToResponse = booking =>
        new KitchenBookingResponse(
            booking.Id,
            booking.Name,
            booking.Email,
            booking.Phone,
            booking.AppointmentDate,
            booking.AppointmentTime,
            booking.Selections,
            booking.Notes,
            booking.Status,
            booking.CreatedAt,
            booking.DecidedAt,
            booking.DecidedBy);
}

public sealed record KitchenBookingResponse(
    Guid Id,
    string Name,
    string Email,
    string Phone,
    DateOnly Date,
    string Time,
    List<string> Selections,
    string? Notes,
    KitchenBookingStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

public sealed record ApproveKitchenBookingRequest
{
    /// <summary>Move the visit to this date; leave out to keep the requested one.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Move the visit to this slot, e.g. "13:00"; leave out to keep the requested one.</summary>
    [MaxLength(5)]
    public string? Time { get; init; }
}

public sealed record DeclineKitchenBookingRequest
{
    /// <summary>An optional note for the customer, added to the email.</summary>
    [MaxLength(2000)]
    public string? Message { get; init; }
}
