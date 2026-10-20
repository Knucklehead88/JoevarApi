using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>The website's Contact form. Anyone can send a message, signed in or not.</summary>
public static class ContactEndpoints
{
    /// <summary>Limits form spam per client IP.</summary>
    public const string RateLimitPolicy = "contact";

    public static void AddContactRateLimit(this RateLimiterOptions options) =>
        options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) }));

    public static IEndpointRouteBuilder MapContactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/contact", SendContactMessageAsync)
            .WithTags("Contact")
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("SendContactMessage")
            .WithSummary("Send a message through the website's Contact form");

        return endpoints;
    }

    private static async Task<Results<NoContent, ValidationProblem>> SendContactMessageAsync(
        ContactMessageRequest request,
        AppDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!PhoneNumbers.TryParseOptional(request.Phone, out var phone, out var problem))
        {
            return problem!;
        }

        db.ContactMessages.Add(new ContactMessage
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            Phone = phone,
            Body = request.Message.Trim()
        });
        await db.SaveChangesAsync(cancellationToken);
        loggerFactory.CreateLogger("Contact").LogInformation("A contact message was received.");

        return TypedResults.NoContent();
    }
}

public sealed record ContactMessageRequest
{
    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public required string Email { get; init; }

    [MaxLength(30)]
    public string? Phone { get; init; }

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public required string Message { get; init; }
}
