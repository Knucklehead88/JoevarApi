/// <summary>One piece of work in a project (the Items table), with the people assigned to it.</summary>
public sealed class ProjectItem
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>What's to be done, e.g. "Remove the old cabinets".</summary>
    public required string Work { get; set; }

    public ProjectType Type { get; set; } = ProjectType.General;

    public ProjectItemStatus Status { get; set; } = ProjectItemStatus.NotStarted;

    /// <summary>Staff or Crew logins doing this part (table ProjectItemAssignees).</summary>
    public List<AppUser> Assignees { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Work item statuses, as on a monday.com board.</summary>
public enum ProjectItemStatus
{
    NotStarted,
    WorkingOnIt,
    Stuck,
    Done
}
