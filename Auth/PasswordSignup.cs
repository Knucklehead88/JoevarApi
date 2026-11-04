using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Email + password for customers: "Create account" (<c>/api/auth/signup</c>) and sign-in (<c>/api/auth/password/login</c>).
/// The login's username starts as the email and the customer's name as the email too; they set both later in their
/// profile. The email isn't verified, so it stays unconfirmed on the login and off the customer record: a Google or
/// Microsoft sign-in with that email never lands in this account (see <see cref="ExternalAccounts"/>).
/// </summary>
public static class PasswordSignupEndpoints
{
    public static IEndpointRouteBuilder MapPasswordSignupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/signup", SignUpAsync)
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy)
            .Produces<AccessTokenResponse>()
            .WithName("PasswordSignup")
            .WithSummary("Create a customer account with an email and password, and sign in");
        endpoints.MapPost("/api/auth/password/login", SignInAsync)
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy)
            .Produces<AccessTokenResponse>()
            .WithName("PasswordLogin")
            .WithSummary("Sign a customer or contractor in with their email and password");

        return endpoints;
    }

    private static async Task<Results<EmptyHttpResult, ValidationProblem, ProblemHttpResult>> SignUpAsync(
        PasswordSignupRequest request,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();

        // One account per person: an email already on file signs in another way. (Also keeps the username free.)
        if (await ExternalAccounts.EmailOnFileAsync(email, db, userManager, cancellationToken) ||
            await userManager.FindByNameAsync(email) is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["An account with this email already exists. Sign in instead."]
            });
        }

        var customer = new Customer { Name = email };
        var user = new AppUser { UserName = email, Email = email };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
        user.CustomerId = customer.Id;

        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            // Password rules (length, digits, ...) and anything else Identity refuses; nothing is kept.
            return TypedResults.ValidationProblem(created.Errors
                .GroupBy(error => error.Code.Contains("Password", StringComparison.Ordinal) ? "password" : "email")
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
        }

        await userManager.AddToRoleAsync(user, AppRoles.Customer);
        await transaction.CommitAsync(cancellationToken);
        loggerFactory.CreateLogger("PasswordSignup").LogInformation("A new customer signed up with a password.");

        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: "pwd");
        return TypedResults.Empty;
    }

    private static async Task<Results<EmptyHttpResult, ProblemHttpResult>> SignInAsync(
        PasswordLoginRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        CancellationToken cancellationToken)
    {
        // Only logins with a password; more than one with this email can't be told apart, so neither signs in.
        var normalizedEmail = userManager.NormalizeEmail(request.Email.Trim());
        var users = await userManager.Users
            .Where(user => user.NormalizedEmail == normalizedEmail && user.PasswordHash != null)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (users.Count != 1)
        {
            return Failed("Failed");
        }

        // Staff sign in with their username and the authenticator code, on their own page.
        var user = users[0];
        var roles = await userManager.GetRolesAsync(user);
        if (roles.Any(StaffAccounts.Roles.Contains))
        {
            return Failed("RequiresTwoFactor");
        }

        // The bearer scheme writes the access/refresh token response, the same shape as /api/auth/login.
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return TypedResults.Empty;
        }

        return Failed(result.RequiresTwoFactor ? "RequiresTwoFactor" : result.IsLockedOut ? "LockedOut" : "Failed");
    }

    private static ProblemHttpResult Failed(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: detail);
}

public sealed record PasswordSignupRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(128)]
    public required string Password { get; init; }
}

public sealed record PasswordLoginRequest
{
    [Required]
    [MaxLength(256)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(128)]
    public required string Password { get; init; }
}
