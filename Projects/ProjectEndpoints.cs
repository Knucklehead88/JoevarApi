using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Customer projects. The office (Admin, Manager, VA) sees and manages them all; Crew see the projects they're on.
/// Creating, editing, deleting and setting the team are office-only. Each project is for a customer record, found by
/// phone or email (or picked) and created when new. Changes are recorded in the project's feed (<see cref="ProjectFeed"/>).
/// </summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects")
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam);

        group.MapGet("/", ListProjectsAsync).WithName("ListProjects").WithSummary("List the customer projects you can see");
        group.MapGet("/{id:guid}", GetProjectAsync).WithName("GetProject").WithSummary("Get a project");
        group.MapPost("/", CreateProjectAsync).RequireAuthorization(AuthPolicies.Staff).WithName("CreateProject").WithSummary("Create a project (office)");
        group.MapPut("/{id:guid}", UpdateProjectAsync).RequireAuthorization(AuthPolicies.Staff).WithName("UpdateProject").WithSummary("Update a project (office)");
        group.MapPut("/{id:guid}/team", UpdateTeamAsync).RequireAuthorization(AuthPolicies.Staff).WithName("UpdateProjectTeam").WithSummary("Set a project's owner and contributors (office)");
        group.MapDelete("/{id:guid}", DeleteProjectAsync).RequireAuthorization(AuthPolicies.Staff).WithName("DeleteProject").WithSummary("Delete a project (office)");

        endpoints.MapGet("/api/project-team", ListTeamLoginsAsync)
            .WithTags("Projects")
            .RequireAuthorization(AuthPolicies.ProjectTeam)
            .WithName("ListProjectTeamLogins")
            .WithSummary("Staff and Crew who can own, contribute to or be assigned on projects");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<ProjectResponse>>> ListProjectsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        Guid? customerId,
        ProjectStatus? status,
        ProjectType? type,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = ProjectAccess.Visible(db, principal).AsNoTracking();
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
            var text = search.Trim();
            var pattern = $"%{text}%";
            query = query.Where(project =>
                EF.Functions.ILike(project.Name, pattern) ||
                (project.Address != null && EF.Functions.ILike(project.Address, pattern)) ||
                EF.Functions.ILike(project.Customer.Name, pattern) ||
                (project.Customer.Email != null && EF.Functions.ILike(project.Customer.Email, pattern)) ||
                (project.Customer.Phone != null && project.Customer.Phone.Number.Contains(text)));
        }

        var result = await query
            .OrderByDescending(project => project.LastActivityAt)
            .Select(ToResponse(db))
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound>> GetProjectAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var project = await ProjectAccess.Visible(db, principal).AsNoTracking()
            .Where(project => project.Id == id)
            .Select(ToResponse(db))
            .FirstOrDefaultAsync(cancellationToken);
        return project is null ? TypedResults.NotFound() : TypedResults.Ok(project);
    }

    private static async Task<Results<Created<ProjectResponse>, ValidationProblem>> CreateProjectAsync(
        ProjectRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        SmsSender sms,
        CancellationToken cancellationToken)
    {
        var author = await db.Users.FirstAsync(user => user.Id == ProjectAccess.UserId(principal), cancellationToken);
        var (customer, problem) = await ResolveCustomerAsync(request, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var project = new Project { Name = "", Customer = customer! };
        Apply(project, request);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        var notes = new List<string> { "Created the project." };
        notes.AddRange(await ApplyConsentAsync(customer!, request.CustomerTexts, author, sms, cancellationToken));
        await feed.RecordChangeAsync(project, author, string.Join("\n", notes), null, cancellationToken);

        return TypedResults.Created($"/api/projects/{project.Id}", await LoadAsync(db, project.Id, cancellationToken));
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound, ValidationProblem>> UpdateProjectAsync(
        Guid id,
        ProjectRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        SmsSender sms,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.Include(project => project.Customer).ThenInclude(customer => customer.Phone)
            .FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        var author = await db.Users.FirstAsync(user => user.Id == ProjectAccess.UserId(principal), cancellationToken);
        // Editing a project edits its own customer, unless another one is picked.
        var (customer, problem) = await ResolveCustomerAsync(request with { CustomerId = request.CustomerId ?? project.CustomerId }, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var notes = new List<string>();
        var before = (project.Name, project.Status, project.Address, Types: string.Join(",", project.Types), project.CustomerId);
        project.Customer = customer!;
        Apply(project, request);
        await db.SaveChangesAsync(cancellationToken);

        if (before.Name != project.Name)
        {
            notes.Add($"Renamed the project to “{project.Name}”.");
        }

        if (before.CustomerId != project.CustomerId)
        {
            notes.Add($"Customer: {customer!.Name}.");
        }

        if (before.Address != project.Address)
        {
            notes.Add(project.Address is null ? "Removed the address." : $"Address: {project.Address}.");
        }

        if (before.Types != string.Join(",", project.Types))
        {
            notes.Add(project.Types.Count == 0 ? "Removed the project types." : $"Project types: {string.Join(", ", project.Types.Select(TypeLabel))}.");
        }

        string? customerText = null;
        if (before.Status != project.Status)
        {
            notes.Add($"Status: {StatusLabel(before.Status)} → {StatusLabel(project.Status)}.");
            customerText = $"the project is now {StatusLabel(project.Status)}.";
        }

        notes.AddRange(await ApplyConsentAsync(customer!, request.CustomerTexts, author, sms, cancellationToken));
        if (notes.Count > 0)
        {
            await feed.RecordChangeAsync(project, author, string.Join("\n", notes), customerText, cancellationToken);
        }

        return TypedResults.Ok(await LoadAsync(db, project.Id, cancellationToken));
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound, ValidationProblem>> UpdateTeamAsync(
        Guid id,
        ProjectTeamRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        ProjectFeed feed,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.Include(project => project.Contributors).Include(project => project.Owner)
            .Include(project => project.Customer)
            .FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        var wanted = request.ContributorIds.Append(request.OwnerId).OfType<string>().Distinct().ToList();
        var logins = await ProjectAccess.TeamLogins(db).Where(user => wanted.Contains(user.Id)).ToListAsync(cancellationToken);
        if (request.OwnerId is not null && logins.All(user => user.Id != request.OwnerId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ownerId"] = ["Choose an active staff or Crew login."] });
        }

        if (request.ContributorIds.Any(contributor => logins.All(user => user.Id != contributor)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["contributorIds"] = ["Choose active staff or Crew logins."] });
        }

        var notes = new List<string>();
        if (project.OwnerId != request.OwnerId)
        {
            var owner = logins.FirstOrDefault(user => user.Id == request.OwnerId);
            project.Owner = owner;
            project.OwnerId = owner?.Id;
            notes.Add(owner is null ? "Removed the owner." : $"Owner: {ProjectFeed.NameOf(owner)}.");
        }

        var contributors = logins.Where(user => request.ContributorIds.Contains(user.Id) && user.Id != request.OwnerId).ToList();
        var added = contributors.Where(user => project.Contributors.All(current => current.Id != user.Id)).ToList();
        var removed = project.Contributors.Where(current => contributors.All(user => user.Id != current.Id)).ToList();
        project.Contributors.RemoveAll(removed.Contains);
        project.Contributors.AddRange(added);
        if (added.Count > 0)
        {
            notes.Add($"Added {string.Join(", ", added.Select(ProjectFeed.NameOf))} as contributor{(added.Count > 1 ? "s" : "")}.");
        }

        if (removed.Count > 0)
        {
            notes.Add($"Removed {string.Join(", ", removed.Select(ProjectFeed.NameOf))} from the contributors.");
        }

        await db.SaveChangesAsync(cancellationToken);
        if (notes.Count > 0)
        {
            var author = await db.Users.FirstAsync(user => user.Id == ProjectAccess.UserId(principal), cancellationToken);
            await feed.RecordChangeAsync(project, author, string.Join("\n", notes), null, cancellationToken);
        }

        return TypedResults.Ok(await LoadAsync(db, project.Id, cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteProjectAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var deleted = await db.Projects.Where(project => project.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static async Task<Ok<List<TeamLoginResponse>>> ListTeamLoginsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var logins = await ProjectAccess.TeamLogins(db)
            .Select(user => new
            {
                user.Id,
                Name = user.DisplayName ?? user.UserName!,
                user.PhoneNumber,
                Roles = db.UserRoles.Where(userRole => userRole.UserId == user.Id).Join(db.Roles, userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name!).ToList(),
                CanText = user.PhoneNumber != null && db.SmsConsents
                    .Where(consent => consent.PhoneNumber == user.PhoneNumber)
                    .OrderByDescending(consent => consent.RecordedAt)
                    .Select(consent => (SmsConsentStatus?)consent.Status)
                    .FirstOrDefault() == SmsConsentStatus.OptedIn
            })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(logins
            .Select(login => new TeamLoginResponse(
                login.Id,
                login.Name,
                ProjectFeed.Handle(login.Name),
                StaffAccounts.Roles.First(login.Roles.Contains),
                login.PhoneNumber,
                login.CanText))
            .OrderBy(login => login.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// The project's customer: the one picked (<see cref="ProjectRequest.CustomerId"/>, whose details the form edits),
    /// else the one with this phone or email, which only gets missing details filled in, else a new one.
    /// </summary>
    private static async Task<(Customer? Customer, ValidationProblem? Problem)> ResolveCustomerAsync(
        ProjectRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var phone = PhoneNumbers.Normalize(request.CustomerPhone);
        if (!string.IsNullOrWhiteSpace(request.CustomerPhone) && phone is null)
        {
            errors["customerPhone"] = ["Enter a valid phone number, e.g. (773) 555-0100 or +1 773 555 0100."];
        }

        if (request.CustomerTexts && string.IsNullOrWhiteSpace(request.CustomerPhone))
        {
            errors["customerPhone"] = ["Add the customer's phone number to text them."];
        }

        if (request.StartsAt is not null && request.EndsAt is not null && request.EndsAt < request.StartsAt)
        {
            errors["endsAt"] = ["The end of the timeline must not be before the start."];
        }

        Customer? customer = null;
        var picked = false;
        if (request.CustomerId is not null)
        {
            customer = await db.Customers.Include(candidate => candidate.Phone).FirstOrDefaultAsync(candidate => candidate.Id == request.CustomerId, cancellationToken);
            picked = customer is not null;
            if (customer is null)
            {
                errors["customerId"] = ["Customer not found."];
            }
        }

        if (errors.Count > 0)
        {
            return (null, TypedResults.ValidationProblem(errors));
        }

        var email = string.IsNullOrWhiteSpace(request.CustomerEmail) ? null : request.CustomerEmail.Trim();
        if (customer is null && phone is not null)
        {
            customer = await db.Customers.Include(candidate => candidate.Phone)
                .Where(candidate => candidate.Phone != null && candidate.Phone.Number == phone)
                .OrderBy(candidate => candidate.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        }

        if (customer is null && email is not null)
        {
            customer = await db.Customers.Include(candidate => candidate.Phone)
                .Where(candidate => candidate.Email != null && EF.Functions.ILike(candidate.Email, email))
                .OrderBy(candidate => candidate.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        }

        if (customer is null)
        {
            customer = new Customer { Name = request.CustomerName.Trim(), Email = email, Phone = phone is null ? null : new ContactPhone { Number = phone } };
            db.Customers.Add(customer);
        }
        else if (picked)
        {
            customer.Name = request.CustomerName.Trim();
            customer.Email = email;
            PhoneNumbers.Replace(db, customer.Phone, phone, newPhone => customer.Phone = newPhone);
        }
        else
        {
            customer.Email ??= email;
            if (customer.Phone is null && phone is not null)
            {
                customer.Phone = new ContactPhone { Number = phone };
            }
        }

        return (customer, null);
    }

    /// <summary>Records the "customer agreed to texts" checkbox when it changed, and texts the opt-in confirmation.</summary>
    private static async Task<List<string>> ApplyConsentAsync(Customer customer, bool texts, AppUser author, SmsSender sms, CancellationToken cancellationToken)
    {
        var phone = customer.Phone?.Number;
        if (phone is null || await sms.IsOptedInAsync(phone, cancellationToken) == texts)
        {
            return [];
        }

        if (await sms.RecordStaffConsentAsync(phone, texts, author.UserName!, cancellationToken))
        {
            var failure = await sms.SendOptInConfirmationAsync(phone, cancellationToken);
            return [$"{customer.Name} agreed to receive texts{(failure is null ? "; the opt-in confirmation was texted." : $" (confirmation not texted: {failure}).")}"];
        }

        return [$"{customer.Name} no longer gets texts."];
    }

    private static void Apply(Project project, ProjectRequest request)
    {
        var types = request.Types.Distinct().ToList();
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{request.CustomerName.Trim()}{(types.Count > 0 ? $" – {string.Join(", ", types.Select(TypeLabel))}" : "")}"
            : request.Name.Trim();
        project.Name = name.Length > 200 ? name[..200] : name;
        project.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        project.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        project.Status = request.Status;
        project.Types = types;
        project.StartsAt = request.StartsAt;
        project.EndsAt = request.EndsAt;
    }

    public static string StatusLabel(ProjectStatus status) => status == ProjectStatus.OnHold ? "On hold" : status.ToString();

    public static string TypeLabel(ProjectType type) => type == ProjectType.Hvac ? "HVAC" : type.ToString();

    private static Task<ProjectResponse> LoadAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking().Where(project => project.Id == id).Select(ToResponse(db)).FirstAsync(cancellationToken);

    private static Expression<Func<Project, ProjectResponse>> ToResponse(AppDbContext db) =>
        project => new ProjectResponse(
            project.Id,
            project.Name,
            project.CustomerId,
            project.Customer.Name,
            project.Customer.Email,
            project.Customer.Phone == null ? null : project.Customer.Phone.Number,
            // Inlined (EF can't translate a helper call here): the latest consent for the customer's phone is an opt-in.
            project.Customer.Phone != null && db.SmsConsents
                .Where(consent => consent.PhoneNumber == project.Customer.Phone.Number)
                .OrderByDescending(consent => consent.RecordedAt)
                .Select(consent => (SmsConsentStatus?)consent.Status)
                .FirstOrDefault() == SmsConsentStatus.OptedIn,
            project.Address,
            project.Description,
            project.Status,
            project.Types,
            project.StartsAt,
            project.EndsAt,
            project.Owner == null ? null : new TeamMember(project.Owner.Id, project.Owner.DisplayName ?? project.Owner.UserName!),
            project.Contributors.Select(user => new TeamMember(user.Id, user.DisplayName ?? user.UserName!)).ToList(),
            project.Items.Count(),
            project.Items.Count(item => item.Status == ProjectItemStatus.Done),
            project.CreatedAt,
            project.LastActivityAt);
}

/// <summary>
/// A project and its customer. <see cref="CustomerId"/> picks an existing customer (and edits their details);
/// without it, the customer with this phone or email is used, or a new one is created. <see cref="CustomerTexts"/>:
/// the customer agreed to receive texts (recorded as consent; required before anything is texted to them).
/// </summary>
public sealed record ProjectRequest
{
    public Guid? CustomerId { get; init; }

    [Required]
    [MaxLength(200)]
    public required string CustomerName { get; init; }

    [EmailAddress]
    [MaxLength(320)]
    public string? CustomerEmail { get; init; }

    [MaxLength(30)]
    public string? CustomerPhone { get; init; }

    public bool CustomerTexts { get; init; }

    /// <summary>Optional; defaults to the customer's name and the project types.</summary>
    [MaxLength(200)]
    public string? Name { get; init; }

    [MaxLength(500)]
    public string? Address { get; init; }

    [MaxLength(4000)]
    public string? Description { get; init; }

    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    public List<ProjectType> Types { get; init; } = [];

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }
}

/// <summary>The owner and contributors: staff or Crew login ids (from <c>GET /api/project-team</c>).</summary>
public sealed record ProjectTeamRequest
{
    public string? OwnerId { get; init; }

    public List<string> ContributorIds { get; init; } = [];
}

public sealed record TeamMember(string Id, string Name);

/// <summary>A staff or Crew login for the pickers. <see cref="CanText"/>: a phone and an opt-in on record.</summary>
public sealed record TeamLoginResponse(string Id, string Name, string Handle, string Role, string? Phone, bool CanText);

public sealed record ProjectResponse(
    Guid Id,
    string Name,
    Guid CustomerId,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    bool CustomerTexts,
    string? Address,
    string? Description,
    ProjectStatus Status,
    List<ProjectType> Types,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    TeamMember? Owner,
    List<TeamMember> Contributors,
    int ItemCount,
    int DoneCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
