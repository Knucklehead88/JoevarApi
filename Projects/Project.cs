public sealed class Project
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public required string Name { get; set; }

    public string? Description { get; set; }

    public string? Address { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Lead;

    /// <summary>Kinds of work in the project, e.g. Roofing and Plumbing.</summary>
    public List<ProjectType> Types { get; set; } = [];

    /// <summary>Timeline: a single date (only <see cref="StartsAt"/>) or a range.</summary>
    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ProjectStatus
{
    Lead,
    Active,
    OnHold,
    Completed,
    Cancelled
}

public enum ProjectType
{
    General,
    Demolition,
    Roofing,
    Plumbing,
    Electrical,
    Hvac,
    Flooring,
    Tile,
    Cabinets,
    Carpentry,
    Drywall,
    Painting,
    Bathroom,
    Kitchen,
    Cleaning,
    Other
}
