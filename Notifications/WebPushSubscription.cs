/// <summary>
/// A browser that agreed to receive push notifications: a staff member's device (<see cref="UserId"/>), or the device a
/// customer booked a kitchen design visit from (<see cref="KitchenBookingId"/>). Deleted with its user or booking, and
/// when the push service says the browser withdrew it.
/// </summary>
public sealed class WebPushSubscription
{
    public Guid Id { get; set; }

    /// <summary>The push service URL the browser gave us (Google, Mozilla, Apple or Microsoft).</summary>
    public required string Endpoint { get; set; }

    /// <summary>The browser's public key (base64url), used to encrypt messages for it.</summary>
    public required string P256dh { get; set; }

    /// <summary>The browser's auth secret (base64url).</summary>
    public required string Auth { get; set; }

    /// <summary>Set for a staff device: it gets the staff alerts.</summary>
    public string? UserId { get; set; }

    /// <summary>Set for a customer's device: it gets the decision on this booking.</summary>
    public Guid? KitchenBookingId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
