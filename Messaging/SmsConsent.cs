/// <summary>One opt-in or opt-out event for a phone number, kept as proof of consent.</summary>
public sealed class SmsConsent
{
    public Guid Id { get; set; }

    public Guid ContactPhoneId { get; set; }

    public ContactPhone ContactPhone { get; set; } = null!;

    public SmsConsentStatus Status { get; set; }

    public SmsConsentSource Source { get; set; }

    /// <summary>The consent wording the person agreed to, or the keyword they texted.</summary>
    public string? Evidence { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SmsConsentStatus
{
    OptedIn,
    OptedOut
}

public enum SmsConsentSource
{
    WebForm,
    Keyword,
    Verbal,
    Import
}
