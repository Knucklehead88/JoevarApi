/// <summary>A "Schedule your free blueprint" booking from the Kitchen Design page.</summary>
public sealed class KitchenBooking
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    /// <summary>E.164.</summary>
    public required string Phone { get; set; }

    /// <summary>The on-site design visit the customer asked for (Joevar's local date and time).</summary>
    public DateOnly AppointmentDate { get; set; }

    /// <summary>One of <see cref="KitchenBookingEndpoints.TimeSlots"/>, e.g. "09:00".</summary>
    public required string AppointmentTime { get; set; }

    /// <summary>The chosen options as "Category: Option", e.g. "Quartz Countertops: Calacatta Alto".</summary>
    public List<string> Selections { get; set; } = [];

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
