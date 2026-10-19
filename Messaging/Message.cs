/// <summary>An SMS sent to or received from a phone number.</summary>
public sealed class Message
{
    public Guid Id { get; set; }

    public MessageDirection Direction { get; set; }

    public required string From { get; set; }

    public required string To { get; set; }

    public required string Body { get; set; }

    public string? TwilioSid { get; set; }

    public string? Status { get; set; }

    public Guid? ContactPhoneId { get; set; }

    public ContactPhone? ContactPhone { get; set; }

    public Guid? ProjectId { get; set; }

    public Project? Project { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum MessageDirection
{
    Inbound,
    Outbound
}
