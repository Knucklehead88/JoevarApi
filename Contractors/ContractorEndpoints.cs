using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

public static class ContractorEndpoints
{
    public static IEndpointRouteBuilder MapContractorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/contractors")
            .WithTags("Contractors")
            .RequireAuthorization(AuthPolicies.Staff);

        group.MapGet("/", ListContractorsAsync).WithName("ListContractors").WithSummary("List contractors");
        group.MapGet("/{id:guid}", GetContractorAsync).WithName("GetContractor").WithSummary("Get a contractor");
        group.MapPost("/", CreateContractorAsync).WithName("CreateContractor").WithSummary("Create a contractor");
        group.MapPut("/{id:guid}", UpdateContractorAsync).WithName("UpdateContractor").WithSummary("Update a contractor");
        group.MapDelete("/{id:guid}", DeleteContractorAsync).WithName("DeleteContractor").WithSummary("Delete a contractor");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<ContractorResponse>>> ListContractorsAsync(
        AppDbContext db,
        ProjectType? trade,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Contractors.AsNoTracking();
        if (trade is not null)
        {
            query = query.Where(contractor => contractor.Trades.Contains(trade.Value));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(contractor =>
                EF.Functions.ILike(contractor.Name, pattern) ||
                (contractor.CompanyName != null && EF.Functions.ILike(contractor.CompanyName, pattern)) ||
                (contractor.Email != null && EF.Functions.ILike(contractor.Email, pattern)) ||
                (contractor.Phone != null && contractor.Phone.Number.Contains(search.Trim())));
        }

        var result = await query
            .OrderBy(contractor => contractor.Name)
            .Select(ToResponse)
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ContractorResponse>, NotFound>> GetContractorAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var contractor = await db.Contractors.AsNoTracking()
            .Where(contractor => contractor.Id == id)
            .Select(ToResponse)
            .FirstOrDefaultAsync(cancellationToken);
        return contractor is null ? TypedResults.NotFound() : TypedResults.Ok(contractor);
    }

    private static async Task<Results<Created<ContractorResponse>, ValidationProblem>> CreateContractorAsync(
        ContractorRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!PhoneNumbers.TryParseOptional(request.Phone, out var phone, out var problem))
        {
            return problem!;
        }

        var contractor = new Contractor
        {
            Name = request.Name.Trim(),
            Phone = phone is null ? null : new ContactPhone { Number = phone }
        };
        Apply(contractor, request);
        db.Contractors.Add(contractor);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/contractors/{contractor.Id}", MapToResponse(contractor));
    }

    private static async Task<Results<Ok<ContractorResponse>, NotFound, ValidationProblem>> UpdateContractorAsync(
        Guid id,
        ContractorRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!PhoneNumbers.TryParseOptional(request.Phone, out var phone, out var problem))
        {
            return problem!;
        }

        var contractor = await db.Contractors.Include(contractor => contractor.Phone)
            .FirstOrDefaultAsync(contractor => contractor.Id == id, cancellationToken);
        if (contractor is null)
        {
            return TypedResults.NotFound();
        }

        Apply(contractor, request);
        PhoneNumbers.Replace(db, contractor.Phone, phone, newPhone => contractor.Phone = newPhone);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(MapToResponse(contractor));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteContractorAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var contractor = await db.Contractors.FirstOrDefaultAsync(contractor => contractor.Id == id, cancellationToken);
        if (contractor is null)
        {
            return TypedResults.NotFound();
        }

        db.Contractors.Remove(contractor);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static void Apply(Contractor contractor, ContractorRequest request)
    {
        contractor.Name = request.Name.Trim();
        contractor.CompanyName = request.CompanyName?.Trim();
        contractor.Email = request.Email?.Trim();
        contractor.Trades = request.Trades.Distinct().ToList();
    }

    private static readonly Expression<Func<Contractor, ContractorResponse>> ToResponse =
        contractor => new ContractorResponse(
            contractor.Id,
            contractor.Name,
            contractor.CompanyName,
            contractor.Email,
            contractor.Phone == null ? null : contractor.Phone.Number,
            contractor.Trades,
            contractor.CreatedAt);

    private static readonly Func<Contractor, ContractorResponse> MapToResponse = ToResponse.Compile();
}

/// <summary>Contractor fields for create and update. Phone accepts any common format and is stored as E.164.</summary>
public sealed record ContractorRequest
{
    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [MaxLength(200)]
    public string? CompanyName { get; init; }

    [EmailAddress]
    [MaxLength(320)]
    public string? Email { get; init; }

    [MaxLength(30)]
    public string? Phone { get; init; }

    public List<ProjectType> Trades { get; init; } = [];
}

public sealed record ContractorResponse(
    Guid Id,
    string Name,
    string? CompanyName,
    string? Email,
    string? Phone,
    List<ProjectType> Trades,
    DateTimeOffset CreatedAt);
