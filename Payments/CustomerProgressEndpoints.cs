using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The customers' progress board (staff): the stages (Paid Deposit, Received Blueprint, Installed Cabinets, Installed
/// Countertop, and any the office adds, renames or reorders), and each customer's completed ones. A customer is on the
/// board once they've reached a stage; a paid deposit puts them there.
/// </summary>
public static class CustomerProgressEndpoints
{
    public static IEndpointRouteBuilder MapCustomerProgressEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var stages = endpoints.MapGroup("/api/customer-stages").WithTags("Customer progress").RequireAuthorization(AuthPolicies.Staff);
        stages.MapGet("/", ListStagesAsync).WithName("ListCustomerStages").WithSummary("The progress stages, in order");
        stages.MapPost("/", AddStageAsync).WithName("AddCustomerStage").WithSummary("Add a stage at the end");
        stages.MapPut("/order", ReorderStagesAsync).WithName("ReorderCustomerStages").WithSummary("Put the stages in this order");
        stages.MapPut("/{id:guid}", RenameStageAsync).WithName("RenameCustomerStage").WithSummary("Rename a stage");
        stages.MapDelete("/{id:guid}", DeleteStageAsync).WithName("DeleteCustomerStage").WithSummary("Delete a stage (not Paid Deposit)");

        var progress = endpoints.MapGroup("/api/customer-progress").WithTags("Customer progress").RequireAuthorization(AuthPolicies.Staff);
        progress.MapGet("/", ListProgressAsync).WithName("ListCustomerProgress").WithSummary("Customers on the board, latest progress first");
        progress.MapPut("/{customerId:guid}/stages/{stageId:guid}", SetStageAsync).WithName("SetCustomerStage").WithSummary("Mark a stage done or not done");

        return endpoints;
    }

    private static async Task<Ok<List<CustomerStageResponse>>> ListStagesAsync(AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await db.CustomerStages.AsNoTracking()
            .OrderBy(stage => stage.Position).ThenBy(stage => stage.CreatedAt)
            .Select(stage => new CustomerStageResponse(stage.Id, stage.Name, stage.Position, stage.Key))
            .ToListAsync(cancellationToken));

    private static async Task<Created<CustomerStageResponse>> AddStageAsync(
        CustomerStageRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var position = await db.CustomerStages.Select(stage => (int?)stage.Position).MaxAsync(cancellationToken) ?? -1;
        var stage = new CustomerStage { Name = request.Name.Trim(), Position = position + 1 };
        db.CustomerStages.Add(stage);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/customer-stages/{stage.Id}", new CustomerStageResponse(stage.Id, stage.Name, stage.Position, stage.Key));
    }

    private static async Task<Results<Ok<CustomerStageResponse>, NotFound>> RenameStageAsync(
        Guid id, CustomerStageRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var stage = await db.CustomerStages.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (stage is null)
        {
            return TypedResults.NotFound();
        }

        stage.Name = request.Name.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new CustomerStageResponse(stage.Id, stage.Name, stage.Position, stage.Key));
    }

    private static async Task<Results<Ok<List<CustomerStageResponse>>, ValidationProblem>> ReorderStagesAsync(
        CustomerStageOrderRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var stages = await db.CustomerStages.ToListAsync(cancellationToken);
        if (request.Ids.Count != stages.Count || stages.Any(stage => !request.Ids.Contains(stage.Id)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = ["List every stage once."] });
        }

        foreach (var stage in stages)
        {
            stage.Position = request.Ids.IndexOf(stage.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(stages.OrderBy(stage => stage.Position)
            .Select(stage => new CustomerStageResponse(stage.Id, stage.Name, stage.Position, stage.Key)).ToList());
    }

    private static async Task<Results<NoContent, NotFound, Conflict<string>>> DeleteStageAsync(
        Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var stage = await db.CustomerStages.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (stage is null)
        {
            return TypedResults.NotFound();
        }

        if (stage.Key is not null)
        {
            return TypedResults.Conflict($"\"{stage.Name}\" is completed by the website itself, so it can be renamed but not deleted.");
        }

        // Its completions go with it (cascade).
        db.CustomerStages.Remove(stage);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<PagedResult<CustomerProgressResponse>>> ListProgressAsync(
        AppDbContext db, string? search, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking().Where(customer => customer.StageCompletions.Any());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            var pattern = $"%{text}%";
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, pattern) ||
                (customer.Email != null && EF.Functions.ILike(customer.Email, pattern)) ||
                (customer.Phone != null && customer.Phone.Number.Contains(text)));
        }

        var result = await query
            .OrderByDescending(customer => customer.StageCompletions.Max(done => done.CompletedAt))
            .Select(ToResponse(db))
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<CustomerProgressResponse>, NotFound>> SetStageAsync(
        Guid customerId,
        Guid stageId,
        CustomerStageDoneRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken)
            || !await db.CustomerStages.AnyAsync(stage => stage.Id == stageId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var done = await db.CustomerStageCompletions.FirstOrDefaultAsync(
            completion => completion.CustomerId == customerId && completion.StageId == stageId, cancellationToken);
        if (request.Done && done is null)
        {
            db.CustomerStageCompletions.Add(new CustomerStageCompletion { CustomerId = customerId, StageId = stageId, CompletedBy = principal.Identity?.Name });
        }
        else if (!request.Done && done is not null)
        {
            db.CustomerStageCompletions.Remove(done);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await db.Customers.AsNoTracking().Where(customer => customer.Id == customerId).Select(ToResponse(db)).FirstAsync(cancellationToken));
    }

    private static System.Linq.Expressions.Expression<Func<Customer, CustomerProgressResponse>> ToResponse(AppDbContext db) =>
        customer => new CustomerProgressResponse(
            customer.Id,
            customer.Name,
            customer.Email,
            customer.Phone == null ? null : customer.Phone.Number,
            customer.StageCompletions.Select(done => new StageCompletionResponse(done.StageId, done.CompletedAt, done.CompletedBy)).ToList(),
            // The latest paid (and not refunded) deposit, from the kitchen bookings.
            db.KitchenBookings
                .Where(booking => booking.CustomerId == customer.Id && booking.PaidAt != null && booking.RefundedAt == null)
                .OrderByDescending(booking => booking.PaidAt)
                .Select(booking => booking.DepositCents)
                .FirstOrDefault(),
            db.KitchenBookings
                .Where(booking => booking.CustomerId == customer.Id && booking.PaidAt != null && booking.RefundedAt == null)
                .OrderByDescending(booking => booking.PaidAt)
                .Select(booking => booking.PaidAt)
                .FirstOrDefault());
}

/// <summary><see cref="Key"/> is set on stages the website completes itself ("deposit").</summary>
public sealed record CustomerStageResponse(Guid Id, string Name, int Position, string? Key);

public sealed record CustomerStageRequest
{
    [Required]
    [MaxLength(100)]
    public required string Name { get; init; }
}

public sealed record CustomerStageOrderRequest
{
    public List<Guid> Ids { get; init; } = [];
}

public sealed record CustomerStageDoneRequest
{
    public bool Done { get; init; }
}

public sealed record StageCompletionResponse(Guid StageId, DateTimeOffset CompletedAt, string? CompletedBy);

/// <summary>A customer on the board: the stages they've reached, and their latest paid deposit.</summary>
public sealed record CustomerProgressResponse(
    Guid CustomerId,
    string Name,
    string? Email,
    string? Phone,
    List<StageCompletionResponse> Completed,
    long? DepositCents,
    DateTimeOffset? DepositPaidAt);
