using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The project chats (the staff app's chat sidebar) and the notifications bell: the activity of every project you can
/// see, newest first; posting a message (with @mentions texted, see <see cref="ProjectFeed"/>); who can be mentioned;
/// and how much is unread.
/// </summary>
public static class ProjectChatEndpoints
{
    public static IEndpointRouteBuilder MapProjectChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/project-activity", ListActivityAsync)
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam)
            .WithName("ListProjectActivity")
            .WithSummary("The chat and changes of your projects (or one), newest first");

        endpoints.MapPost("/api/projects/{projectId:guid}/messages", PostMessageAsync)
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam)
            .WithName("PostProjectMessage")
            .WithSummary("Post in a project's chat; @mentioned people are texted");

        endpoints.MapGet("/api/projects/{projectId:guid}/mentions", ListMentionsAsync)
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam)
            .WithName("ListProjectMentions")
            .WithSummary("Who can be @mentioned in a project's chat");

        endpoints.MapGet("/api/notifications", GetNotificationsAsync)
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam)
            .WithName("GetNotifications")
            .WithSummary("Unread count and the latest activity on your projects by others");

        endpoints.MapPost("/api/notifications/seen", MarkSeenAsync)
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam)
            .WithName("MarkNotificationsSeen")
            .WithSummary("Mark your notifications as read");

        return endpoints;
    }

    /// <summary>
    /// <paramref name="before"/> pages back through older entries; <paramref name="after"/> fetches only newer ones
    /// (polling). At most <paramref name="limit"/> (default 50, max 200).
    /// </summary>
    private static async Task<Ok<List<ProjectActivityResponse>>> ListActivityAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        Guid? projectId,
        DateTimeOffset? before,
        DateTimeOffset? after,
        int? limit,
        CancellationToken cancellationToken)
    {
        var projects = ProjectAccess.Visible(db, principal).Select(project => project.Id);
        var query = db.ProjectActivity.AsNoTracking().Where(activity => projects.Contains(activity.ProjectId));
        if (projectId is not null)
        {
            query = query.Where(activity => activity.ProjectId == projectId);
        }

        if (before is not null)
        {
            query = query.Where(activity => activity.CreatedAt < before);
        }

        if (after is not null)
        {
            query = query.Where(activity => activity.CreatedAt > after);
        }

        var items = await query
            .OrderByDescending(activity => activity.CreatedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .Select(ToResponse)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<ProjectActivityResponse>, NotFound>> PostMessageAsync(
        Guid projectId,
        ProjectMessageRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        CancellationToken cancellationToken)
    {
        var project = await ProjectAccess.Visible(db, principal)
            .Include(project => project.Customer).ThenInclude(customer => customer.Phone)
            .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        var author = await db.Users.FirstAsync(user => user.Id == ProjectAccess.UserId(principal), cancellationToken);
        var activity = await feed.PostMessageAsync(project, author, request.Text.Trim(), ProjectActivityKind.Message, cancellationToken);
        return TypedResults.Ok(await db.ProjectActivity.AsNoTracking().Where(entry => entry.Id == activity.Id).Select(ToResponse).FirstAsync(cancellationToken));
    }

    private static async Task<Results<Ok<List<MentionResponse>>, NotFound>> ListMentionsAsync(
        Guid projectId,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        SmsSender sms,
        CancellationToken cancellationToken)
    {
        var project = await ProjectAccess.Visible(db, principal)
            .Include(project => project.Customer).ThenInclude(customer => customer.Phone)
            .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        var me = ProjectAccess.UserId(principal);
        var mentions = new List<MentionResponse>();
        foreach (var candidate in await feed.MentionCandidatesAsync(project, cancellationToken))
        {
            if (candidate.UserId == me)
            {
                continue;
            }

            var reason = candidate.Phone is null ? "no phone number"
                : !sms.IsConfigured ? "texting isn't set up"
                : !await sms.IsOptedInAsync(candidate.Phone, cancellationToken) ? "hasn't agreed to texts"
                : null;
            mentions.Add(new MentionResponse(candidate.Handle, candidate.Name, candidate.UserId, candidate.IsCustomer, reason is null, reason));
        }

        return TypedResults.Ok(mentions);
    }

    private static async Task<Ok<NotificationsResponse>> GetNotificationsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var me = ProjectAccess.UserId(principal);
        var seenAt = await db.Users.Where(user => user.Id == me).Select(user => user.NotificationsSeenAt).FirstAsync(cancellationToken);
        var projects = ProjectAccess.Visible(db, principal).Select(project => project.Id);
        var others = db.ProjectActivity.AsNoTracking()
            .Where(activity => projects.Contains(activity.ProjectId) && (activity.AuthorUserId == null || activity.AuthorUserId != me));
        var unread = await (seenAt is null ? others : others.Where(activity => activity.CreatedAt > seenAt)).CountAsync(cancellationToken);
        var latest = await others.OrderByDescending(activity => activity.CreatedAt).Take(20).Select(ToResponse).ToListAsync(cancellationToken);
        return TypedResults.Ok(new NotificationsResponse(unread, seenAt, latest));
    }

    private static async Task<NoContent> MarkSeenAsync(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var me = ProjectAccess.UserId(principal);
        await db.Users.Where(user => user.Id == me)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.NotificationsSeenAt, DateTimeOffset.UtcNow), cancellationToken);
        return TypedResults.NoContent();
    }

    private static readonly System.Linq.Expressions.Expression<Func<ProjectActivity, ProjectActivityResponse>> ToResponse =
        activity => new ProjectActivityResponse(
            activity.Id,
            activity.ProjectId,
            activity.Project.Name,
            activity.Kind,
            activity.AuthorUserId,
            activity.AuthorName,
            activity.FromCustomer,
            activity.Text,
            activity.Texted,
            activity.NotTexted,
            activity.CreatedAt);
}

public sealed record ProjectMessageRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(1500)]
    public required string Text { get; init; }
}

/// <summary>One feed entry. <see cref="Texted"/>: who got it by SMS; <see cref="NotTexted"/>: who didn't, and why.</summary>
public sealed record ProjectActivityResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    ProjectActivityKind Kind,
    string? AuthorUserId,
    string AuthorName,
    bool FromCustomer,
    string Text,
    List<string> Texted,
    List<string> NotTexted,
    DateTimeOffset CreatedAt);

/// <summary>Someone to @mention: write <c>@{Handle}</c>. <see cref="CanText"/> false gives the <see cref="Reason"/>.</summary>
public sealed record MentionResponse(string Handle, string Name, string? UserId, bool IsCustomer, bool CanText, string? Reason);

/// <summary>Activity by others on your projects: how much is new since <see cref="SeenAt"/>, and the latest 20.</summary>
public sealed record NotificationsResponse(int Unread, DateTimeOffset? SeenAt, List<ProjectActivityResponse> Items);
