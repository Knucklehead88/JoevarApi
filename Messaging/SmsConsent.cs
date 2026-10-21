/// <summary>One opt-in or opt-out event for a phone number, kept as proof of consent.</summary>
public sealed class SmsConsent
{
    public Guid Id { get; set; }

    /// <summary>The number (E.164) the consent is for; it doesn't have to belong to a customer or contractor yet.</summary>
    public required string PhoneNumber { get; set; }

    /// <summary>The customer's/contractor's phone record, when the number was on file at the time.</summary>
    public Guid? ContactPhoneId { get; set; }

    public ContactPhone? ContactPhone { get; set; }

    public SmsConsentStatus Status { get; set; }

    public SmsConsentSource Source { get; set; }

    /// <summary>The consent wording the person agreed to, or the keyword they texted.</summary>
    public string? Evidence { get; set; }

    /// <summary>Which version of the consent wording was shown (web form), e.g. "2026-10-8".</summary>
    public string? EvidenceVersion { get; set; }

    /// <summary>The page the consent was given on (web form).</summary>
    public string? SourceUrl { get; set; }

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
