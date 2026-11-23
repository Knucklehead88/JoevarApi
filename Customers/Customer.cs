public sealed class Customer
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Project> Projects { get; set; } = [];

    public ContactPhone? Phone { get; set; }

    /// <summary>The customer in Stripe ("cus_…"), created at their first payment or saved card.</summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>The progress stages they've reached (Paid Deposit, Received Blueprint, …).</summary>
    public List<CustomerStageCompletion> StageCompletions { get; set; } = [];
}
