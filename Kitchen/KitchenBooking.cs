/// <summary>A "Schedule your free blueprint" booking from the Kitchen Design page.</summary>
public sealed class KitchenBooking
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    /// <summary>E.164.</summary>
    public required string Phone { get; set; }

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
}

/// <summary>New bookings wait for staff, who approve (confirming the visit) or decline them.</summary>
public enum KitchenBookingStatus
{
    Pending,
    Approved,
    Declined
}
