using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/customers")
            .WithTags("Customers")
            .RequireAuthorization(AuthPolicies.Staff);

        group.MapGet("/", ListCustomersAsync).WithName("ListCustomers").WithSummary("List customers");
        group.MapGet("/{id:guid}", GetCustomerAsync).WithName("GetCustomer").WithSummary("Get a customer");
        group.MapPost("/", CreateCustomerAsync).WithName("CreateCustomer").WithSummary("Create a customer");
        group.MapPut("/{id:guid}", UpdateCustomerAsync).WithName("UpdateCustomer").WithSummary("Update a customer");
        group.MapDelete("/{id:guid}", DeleteCustomerAsync).WithName("DeleteCustomer").WithSummary("Delete a customer");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<CustomerResponse>>> ListCustomersAsync(
        AppDbContext db,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, pattern) ||
                (customer.Email != null && EF.Functions.ILike(customer.Email, pattern)) ||
                (customer.Phone != null && customer.Phone.Number.Contains(search.Trim())));
        }

        var result = await query
            .OrderBy(customer => customer.Name)
            .Select(ToResponse)
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<CustomerResponse>, NotFound>> GetCustomerAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking()
            .Where(customer => customer.Id == id)
            .Select(ToResponse)
            .FirstOrDefaultAsync(cancellationToken);
        return customer is null ? TypedResults.NotFound() : TypedResults.Ok(customer);
    }

    private static async Task<Results<Created<CustomerResponse>, ValidationProblem>> CreateCustomerAsync(
        CustomerRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!PhoneNumbers.TryParseOptional(request.Phone, out var phone, out var problem))
        {
            return problem!;
        }

        var customer = new Customer
        {
            Name = request.Name.Trim(),
            Email = request.Email?.Trim(),
            Phone = phone is null ? null : new ContactPhone { Number = phone }
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/customers/{customer.Id}", MapToResponse(customer));
    }

    private static async Task<Results<Ok<CustomerResponse>, NotFound, ValidationProblem>> UpdateCustomerAsync(
        Guid id,
        CustomerRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!PhoneNumbers.TryParseOptional(request.Phone, out var phone, out var problem))
        {
            return problem!;
        }

        var customer = await db.Customers.Include(customer => customer.Phone)
            .FirstOrDefaultAsync(customer => customer.Id == id, cancellationToken);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        customer.Name = request.Name.Trim();
        customer.Email = request.Email?.Trim();
        PhoneNumbers.Replace(db, customer.Phone, phone, newPhone => customer.Phone = newPhone);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(MapToResponse(customer));
    }

    private static async Task<Results<NoContent, NotFound, Conflict<string>>> DeleteCustomerAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(customer => customer.Id == id, cancellationToken);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        if (await db.Projects.AnyAsync(project => project.CustomerId == id, cancellationToken))
        {
            return TypedResults.Conflict("The customer has projects; delete or reassign them first.");
        }

        db.Customers.Remove(customer);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static readonly System.Linq.Expressions.Expression<Func<Customer, CustomerResponse>> ToResponse =
        customer => new CustomerResponse(
            customer.Id,
            customer.Name,
            customer.Email,
            customer.Phone == null ? null : customer.Phone.Number,
            customer.CreatedAt);

    private static readonly Func<Customer, CustomerResponse> MapToResponse = ToResponse.Compile();
}

/// <summary>Customer fields for create and update. Phone accepts any common format and is stored as E.164.</summary>
public sealed record CustomerRequest
{
    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [EmailAddress]
    [MaxLength(320)]
    public string? Email { get; init; }

    [MaxLength(30)]
    public string? Phone { get; init; }
}

public sealed record CustomerResponse(Guid Id, string Name, string? Email, string? Phone, DateTimeOffset CreatedAt);
