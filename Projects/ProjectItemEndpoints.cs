using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A project's work items (the Items table): work, type, status and the people assigned. Anyone who can see the
/// project (the office, or Crew on it) can add, change and remove items. Each change goes into the project's feed,
/// and new items, status changes and removals are also texted to the customer when they agreed to texts.
/// </summary>
public static class ProjectItemEndpoints
{
    public static IEndpointRouteBuilder MapProjectItemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects/{projectId:guid}/items")
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam);

        group.MapGet("/", ListItemsAsync).WithName("ListProjectItems").WithSummary("List a project's items");
        group.MapPost("/", CreateItemAsync).WithName("CreateProjectItem").WithSummary("Add an item");
        group.MapPut("/{id:guid}", UpdateItemAsync).WithName("UpdateProjectItem").WithSummary("Change an item");
        group.MapDelete("/{id:guid}", DeleteItemAsync).WithName("DeleteProjectItem").WithSummary("Remove an item");

        return endpoints;
    }

    private static async Task<Results<Ok<List<ProjectItemResponse>>, NotFound>> ListItemsAsync(
        Guid projectId,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await ProjectAccess.Visible(db, principal).AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var items = await db.ProjectItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new ProjectItemResponse(
                item.Id,
                item.Work,
                item.Type,
                item.Status,
                item.Assignees.Select(user => new TeamMember(user.Id, user.DisplayName ?? user.UserName!)).ToList(),
                item.CreatedAt,
                item.UpdatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Created<ProjectItemResponse>, NotFound, ValidationProblem>> CreateItemAsync(
        Guid projectId,
        ProjectItemRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        CancellationToken cancellationToken)
    {
        var project = await LoadProjectAsync(db, principal, projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        var (assignees, problem) = await AssigneesAsync(db, request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var item = new ProjectItem
        {
            Project = project,
            Work = request.Work.Trim(),
            Type = request.Type,
            Status = request.Status,
            Assignees = assignees
        };
        db.ProjectItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        var author = await AuthorAsync(db, principal, cancellationToken);
        var people = assignees.Count > 0 ? $", assigned to {string.Join(", ", assignees.Select(ProjectFeed.NameOf))}" : "";
        await feed.RecordChangeAsync(
            project,
            author,
            $"Added “{item.Work}” ({ProjectEndpoints.TypeLabel(item.Type)}, {StatusLabel(item.Status)}{people}).",
            $"added “{item.Work}” ({StatusLabel(item.Status)}).",
            cancellationToken);

        return TypedResults.Created($"/api/projects/{projectId}/items/{item.Id}", ToResponse(item));
    }

    private static async Task<Results<Ok<ProjectItemResponse>, NotFound, ValidationProblem>> UpdateItemAsync(
        Guid projectId,
        Guid id,
        ProjectItemRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        CancellationToken cancellationToken)
    {
        var project = await LoadProjectAsync(db, principal, projectId, cancellationToken);
        var item = project is null
            ? null
            : await db.ProjectItems.Include(item => item.Assignees).FirstOrDefaultAsync(item => item.Id == id && item.ProjectId == projectId, cancellationToken);
        if (project is null || item is null)
        {
            return TypedResults.NotFound();
        }

        var (assignees, problem) = await AssigneesAsync(db, request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var notes = new List<string>();
        var work = request.Work.Trim();
        string? customerText = null;
        if (item.Work != work)
        {
            notes.Add($"renamed it to “{work}”");
        }

        if (item.Type != request.Type)
        {
            notes.Add($"type {ProjectEndpoints.TypeLabel(item.Type)} → {ProjectEndpoints.TypeLabel(request.Type)}");
        }

        if (item.Status != request.Status)
        {
            notes.Add($"status {StatusLabel(item.Status)} → {StatusLabel(request.Status)}");
            customerText = $"“{work}” is now {StatusLabel(request.Status)}.";
        }

        var added = assignees.Where(user => item.Assignees.All(current => current.Id != user.Id)).ToList();
        var removed = item.Assignees.Where(current => assignees.All(user => user.Id != current.Id)).ToList();
        if (added.Count > 0)
        {
            notes.Add($"assigned {string.Join(", ", added.Select(ProjectFeed.NameOf))}");
        }

        if (removed.Count > 0)
        {
            notes.Add($"unassigned {string.Join(", ", removed.Select(ProjectFeed.NameOf))}");
        }

        var oldWork = item.Work;
        item.Work = work;
        item.Type = request.Type;
        item.Status = request.Status;
        item.Assignees.RemoveAll(removed.Contains);
        item.Assignees.AddRange(added);
        if (notes.Count > 0)
        {
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (notes.Count > 0)
        {
            var author = await AuthorAsync(db, principal, cancellationToken);
            var text = $"“{oldWork}”: {string.Join(", ", notes)}.";
            await feed.RecordChangeAsync(project, author, char.ToUpperInvariant(text[0]) + text[1..], customerText, cancellationToken);
        }

        return TypedResults.Ok(ToResponse(item));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteItemAsync(
        Guid projectId,
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        CancellationToken cancellationToken)
    {
        var project = await LoadProjectAsync(db, principal, projectId, cancellationToken);
        var item = project is null ? null : await db.ProjectItems.FirstOrDefaultAsync(item => item.Id == id && item.ProjectId == projectId, cancellationToken);
        if (project is null || item is null)
        {
            return TypedResults.NotFound();
        }

        db.ProjectItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        var author = await AuthorAsync(db, principal, cancellationToken);
        await feed.RecordChangeAsync(project, author, $"Removed “{item.Work}”.", $"removed “{item.Work}” from the plan.", cancellationToken);
        return TypedResults.NoContent();
    }

    public static string StatusLabel(ProjectItemStatus status) => status switch
    {
        ProjectItemStatus.NotStarted => "Not started",
        ProjectItemStatus.WorkingOnIt => "Working on it",
        _ => status.ToString()
    };

    private static Task<Project?> LoadProjectAsync(AppDbContext db, ClaimsPrincipal principal, Guid projectId, CancellationToken cancellationToken) =>
        ProjectAccess.Visible(db, principal)
            .Include(project => project.Customer).ThenInclude(customer => customer.Phone)
            .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);

    private static Task<AppUser> AuthorAsync(AppDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        db.Users.FirstAsync(user => user.Id == ProjectAccess.UserId(principal), cancellationToken);

    private static async Task<(List<AppUser> Assignees, ValidationProblem? Problem)> AssigneesAsync(
        AppDbContext db, ProjectItemRequest request, CancellationToken cancellationToken)
    {
        var ids = request.AssigneeIds.Distinct().ToList();
        var assignees = await ProjectAccess.TeamLogins(db).Where(user => ids.Contains(user.Id)).ToListAsync(cancellationToken);
        return assignees.Count == ids.Count
            ? (assignees, null)
            : ([], TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assigneeIds"] = ["Choose active staff or Crew logins."] }));
    }

    private static ProjectItemResponse ToResponse(ProjectItem item) => new(
        item.Id,
        item.Work,
        item.Type,
        item.Status,
        item.Assignees.Select(user => new TeamMember(user.Id, ProjectFeed.NameOf(user))).ToList(),
        item.CreatedAt,
        item.UpdatedAt);
}

public sealed record ProjectItemRequest
{
    [Required]
    [MaxLength(500)]
    public required string Work { get; init; }

    public ProjectType Type { get; init; } = ProjectType.General;

    public ProjectItemStatus Status { get; init; } = ProjectItemStatus.NotStarted;

    /// <summary>Staff or Crew login ids (from <c>GET /api/project-team</c>).</summary>
    public List<string> AssigneeIds { get; init; } = [];
}

public sealed record ProjectItemResponse(
    Guid Id,
    string Work,
    ProjectType Type,
    ProjectItemStatus Status,
    List<TeamMember> Assignees,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
