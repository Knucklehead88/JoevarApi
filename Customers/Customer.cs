public sealed class Customer
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Project> Projects { get; set; } = [];

    public ContactPhone? Phone { get; set; }
}
