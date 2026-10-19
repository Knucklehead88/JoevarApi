public sealed class Contractor
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? CompanyName { get; set; }

    public string? Email { get; set; }

    /// <summary>Trades the contractor works in, e.g. "Plumbing", "Electrical".</summary>
    public List<string> Trades { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ContactPhone? Phone { get; set; }
}
