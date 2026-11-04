/// <summary>
/// One entry in a project's feed, which is also its chat: a change (status, items, team), a message posted in the app,
/// or a text received from the customer or a team member. <see cref="Texted"/> lists who it was sent to by SMS.
/// </summary>
public sealed class ProjectActivity
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public ProjectActivityKind Kind { get; set; }

    /// <summary>The staff login that wrote it or made the change; null for the customer's texts.</summary>
    public string? AuthorUserId { get; set; }

    /// <summary>The author's name at the time (the customer's name for their texts).</summary>
    public required string AuthorName { get; set; }

    /// <summary>A text from the customer.</summary>
    public bool FromCustomer { get; set; }

    public required string Text { get; set; }

    /// <summary>Who got it by SMS, e.g. "Jane Doe (customer)".</summary>
    public List<string> Texted { get; set; } = [];

    /// <summary>Who it couldn't be texted to and why, e.g. "Maria: no phone number".</summary>
    public List<string> NotTexted { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ProjectActivityKind
{
    /// <summary>Something changed: the project, its items or its team.</summary>
    Change,

    /// <summary>Written in the chat.</summary>
    Message,

    /// <summary>Received by text, from the customer or a team member.</summary>
    Sms
}
