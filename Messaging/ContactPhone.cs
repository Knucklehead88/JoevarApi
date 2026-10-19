/// <summary>A phone number in E.164 format, belonging to a customer or a contractor.</summary>
public sealed class ContactPhone
{
    public Guid Id { get; set; }

    public required string Number { get; set; }

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? ContractorId { get; set; }

    public Contractor? Contractor { get; set; }

    /// <summary>Consent history; the newest record is the current status.</summary>
    public List<SmsConsent> Consents { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
