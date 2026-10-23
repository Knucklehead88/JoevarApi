using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The website's contractor registration: anyone can apply. The application is saved, emailed to the office (the
/// contractor as Reply-To) and acknowledged to the contractor; staff add them as a contractor after review.
/// </summary>
public static class ContractorApplicationEndpoints
{
    /// <summary>The licenses the form offers.</summary>
    public static readonly string[] Licenses =
    [
        "General Contractor", "Plumbing", "Electrical", "HVAC", "Roofing", "Home Improvement", "Handyman",
        "Asbestos/Lead Abatement", "Other",
    ];

    public static IEndpointRouteBuilder MapContractorApplicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/contractor-applications", ApplyAsync)
            .WithTags("Contractors")
            .RequireRateLimiting(ContactEndpoints.RateLimitPolicy)
            .WithName("ApplyAsContractor")
            .WithSummary("Register as a contractor (reviewed by staff)");

        endpoints.MapGet("/api/contractor-applications", ListApplicationsAsync)
            .WithTags("Contractors")
            .RequireAuthorization(AuthPolicies.Staff)
            .WithName("ListContractorApplications")
            .WithSummary("List contractor registrations, newest first");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<ContractorApplicationResponse>>> ListApplicationsAsync(
        AppDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var result = await db.ContractorApplications.AsNoTracking()
            .OrderByDescending(application => application.CreatedAt)
            .Select(application => new ContractorApplicationResponse(
                application.Id,
                application.Email,
                application.FullName,
                application.CompanyName,
                application.Professions,
                application.State,
                application.City,
                application.Licenses,
                application.CreatedAt))
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<NoContent, ValidationProblem>> ApplyAsync(
        ContractorApplicationRequest request,
        AppDbContext db,
        UsCityDirectory cities,
        OfficeEmailSender emailSender,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.Professions.Count == 0)
        {
            errors["professions"] = ["Choose at least one profession."];
        }

        var unknownLicenses = request.Licenses.Where(license => !Licenses.Contains(license)).ToList();
        if (unknownLicenses.Count > 0)
        {
            errors["licenses"] = [$"Unknown license: {string.Join(", ", unknownLicenses)}."];
        }

        var state = UsCityDirectory.FindState(request.State);
        var city = request.City.Trim();
        if (state is null)
        {
            errors["state"] = ["Choose a US state."];
        }
        else
        {
            // Unknown when the city service is down: accepted as typed rather than refusing the registration.
            var found = await cities.FindCityAsync(state, city, cancellationToken);
            if (found == "")
            {
                errors["city"] = [$"Choose a city in {state}."];
            }
            else if (found is not null)
            {
                city = found;
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var application = new ContractorApplication
        {
            Email = request.Email.Trim(),
            FullName = request.FullName.Trim(),
            CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? null : request.CompanyName.Trim(),
            Professions = request.Professions.Distinct().ToList(),
            State = state!,
            City = city,
            Licenses = request.Licenses.Distinct().ToList()
        };
        db.ContractorApplications.Add(application);
        await db.SaveChangesAsync(cancellationToken);
        var logger = loggerFactory.CreateLogger("ContractorApplication");
        logger.LogInformation("A contractor registration was received.");

        var professions = string.Join(", ", application.Professions);
        var licenses = application.Licenses.Count > 0 ? string.Join(", ", application.Licenses) : "-";

        // Saved first, so a mail problem never loses a registration or shows the contractor an error.
        try
        {
            await emailSender.SendAsync(
                $"Contractor registration: {application.FullName}",
                $"""
                New contractor registration from the Joevar website.

                Name:        {application.FullName}
                Company:     {application.CompanyName ?? "-"}
                Email:       {application.Email}
                Location:    {application.City}, {application.State}
                Professions: {professions}
                Licenses:    {licenses}

                Reply to this email to answer {application.FullName}.
                """,
                application.FullName,
                application.Email,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Contractor application {Id} was saved but could not be emailed.", application.Id);
        }

        try
        {
            await emailSender.SendToCustomerAsync(
                application.FullName,
                application.Email,
                "We received your contractor registration",
                $"""
                Hi {application.FullName},

                Thank you for registering as a contractor with Joevar. We've received your details and will contact
                you after reviewing them.

                Location:    {application.City}, {application.State}
                Professions: {professions}
                Licenses:    {licenses}

                To change anything, just reply to this email.

                Joevar
                """,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Contractor application {Id} was saved but the applicant's email could not be sent.", application.Id);
        }

        return TypedResults.NoContent();
    }
}

public sealed record ContractorApplicationResponse(
    Guid Id,
    string Email,
    string FullName,
    string? CompanyName,
    List<ProjectType> Professions,
    string State,
    string City,
    List<string> Licenses,
    DateTimeOffset CreatedAt);

public sealed record ContractorApplicationRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(200)]
    public required string FullName { get; init; }

    [MaxLength(200)]
    public string? CompanyName { get; init; }

    public List<ProjectType> Professions { get; init; } = [];

    /// <summary>A US state's full name, e.g. "Illinois".</summary>
    [Required]
    [MaxLength(40)]
    public required string State { get; init; }

    [Required]
    [MaxLength(100)]
    public required string City { get; init; }

    [MaxLength(20)]
    public List<string> Licenses { get; init; } = [];
}
