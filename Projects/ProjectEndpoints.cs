using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects")
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.Staff);

        group.MapGet("/", ListProjectsAsync).WithName("ListProjects").WithSummary("List projects");
        group.MapGet("/{id:guid}", GetProjectAsync).WithName("GetProject").WithSummary("Get a project");
        group.MapPost("/", CreateProjectAsync).WithName("CreateProject").WithSummary("Create a project");
        group.MapPut("/{id:guid}", UpdateProjectAsync).WithName("UpdateProject").WithSummary("Update a project");
        group.MapDelete("/{id:guid}", DeleteProjectAsync).WithName("DeleteProject").WithSummary("Delete a project");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<ProjectResponse>>> ListProjectsAsync(
        AppDbContext db,
        Guid? customerId,
        ProjectStatus? status,
        ProjectType? type,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Projects.AsNoTracking();
        if (customerId is not null)
        {
            query = query.Where(project => project.CustomerId == customerId);
        }

        if (status is not null)
        {
            query = query.Where(project => project.Status == status);
        }

        if (type is not null)
        {
            query = query.Where(project => project.Types.Contains(type.Value));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(project =>
                EF.Functions.ILike(project.Name, pattern) ||
                (project.Address != null && EF.Functions.ILike(project.Address, pattern)) ||
                EF.Functions.ILike(project.Customer.Name, pattern));
        }

        var result = await query
            .OrderByDescending(project => project.CreatedAt)
            .Select(ToResponse)
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound>> GetProjectAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(project => project.Id == id)
            .Select(ToResponse)
            .FirstOrDefaultAsync(cancellationToken);
        return project is null ? TypedResults.NotFound() : TypedResults.Ok(project);
    }

    private static async Task<Results<Created<ProjectResponse>, ValidationProblem>> CreateProjectAsync(
        ProjectRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(customer => customer.Id == request.CustomerId, cancellationToken);
        if (Validate(request, customer) is { } problem)
        {
            return problem;
        }

        var project = new Project { Name = request.Name.Trim(), Customer = customer! };
        Apply(project, request);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/projects/{project.Id}", MapToResponse(project));
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound, ValidationProblem>> UpdateProjectAsync(
        Guid id,
        ProjectRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        var customer = await db.Customers.FirstOrDefaultAsync(customer => customer.Id == request.CustomerId, cancellationToken);
        if (Validate(request, customer) is { } problem)
        {
            return problem;
        }

        project.Customer = customer!;
        Apply(project, request);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(MapToResponse(project));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteProjectAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var deleted = await db.Projects.Where(project => project.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static ValidationProblem? Validate(ProjectRequest request, Customer? customer)
    {
        var errors = new Dictionary<string, string[]>();
        if (customer is null)
        {
            errors["customerId"] = ["Customer not found."];
        }

        if (request.StartsAt is not null && request.EndsAt is not null && request.EndsAt < request.StartsAt)
        {
            errors["endsAt"] = ["The end of the timeline must not be before the start."];
        }

        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors);
    }

    private static void Apply(Project project, ProjectRequest request)
    {
        project.Name = request.Name.Trim();
        project.Description = request.Description?.Trim();
        project.Address = request.Address?.Trim();
        project.Status = request.Status;
        project.Types = request.Types.Distinct().ToList();
        project.StartsAt = request.StartsAt;
        project.EndsAt = request.EndsAt;
    }

    private static readonly Expression<Func<Project, ProjectResponse>> ToResponse =
        project => new ProjectResponse(
            project.Id,
            project.CustomerId,
            project.Customer.Name,
            project.Name,
            project.Description,
            project.Address,
            project.Status,
            project.Types,
            project.StartsAt,
            project.EndsAt,
            project.CreatedAt);

    private static readonly Func<Project, ProjectResponse> MapToResponse = ToResponse.Compile();
}

/// <summary>Project fields for create and update. The timeline is a single date (StartsAt) or a range.</summary>
public sealed record ProjectRequest
{
    [Required]
    public required Guid CustomerId { get; init; }

    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    public string? Description { get; init; }

    [MaxLength(500)]
    public string? Address { get; init; }

    public ProjectStatus Status { get; init; } = ProjectStatus.Lead;

    public List<ProjectType> Types { get; init; } = [];

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }
}

public sealed record ProjectResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string Name,
    string? Description,
    string? Address,
    ProjectStatus Status,
    List<ProjectType> Types,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt);
