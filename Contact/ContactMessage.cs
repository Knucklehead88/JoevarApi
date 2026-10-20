/// <summary>A message sent through the website's Contact form.</summary>
public sealed class ContactMessage
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    /// <summary>E.164, when given.</summary>
    public string? Phone { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
