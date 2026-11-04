/// <summary>
/// A customer project ("Customer Projects" in the staff app): the customer, the address, the kinds of work, an owner
/// and contributors (staff or Crew), its work items, and an activity feed that is also the project's chat.
/// </summary>
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

    /// <summary>The staff or Crew login responsible for the project.</summary>
    public string? OwnerId { get; set; }

    public AppUser? Owner { get; set; }

    /// <summary>Other staff or Crew working on it (table ProjectContributors).</summary>
    public List<AppUser> Contributors { get; set; } = [];

    public List<ProjectItem> Items { get; set; } = [];

    public List<ProjectActivity> Activity { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the feed last got an entry; orders the list and picks the project a texted reply belongs to.</summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
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
