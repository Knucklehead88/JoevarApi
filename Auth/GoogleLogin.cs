using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Sign in (and sign up) with Google for customers and contractors. One account per person: the Google account
/// is matched, in order, to a login already linked to it, a login with the same email, a customer/contractor
/// with the same email (whose login is created), and only then a new customer is created (without a phone).
/// </summary>
public static class GoogleLoginEndpoints
{
    public const string LoginProvider = "Google";

    public static IEndpointRouteBuilder MapGoogleLoginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/google", LoginAsync)
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy)
            .Produces<AccessTokenResponse>()
            .WithName("GoogleLogin")
            .WithSummary("Exchange a Google access token for a bearer token, signing up a new customer if needed");

        return endpoints;
    }

    private static async Task<Results<EmptyHttpResult, UnauthorizedHttpResult, ProblemHttpResult>> LoginAsync(
        GoogleTokenRequest request,
        GoogleTokenValidator validator,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("GoogleLogin");
        var google = await validator.ValidateAsync(request.AccessToken, cancellationToken);
        if (google is null)
        {
            return TypedResults.Unauthorized();
        }

        var user = await userManager.FindByLoginAsync(LoginProvider, google.Subject);
        if (user is null)
        {
            var found = await FindOrCreateUserAsync(google, db, userManager, logger, cancellationToken);
            if (found.Problem is not null)
            {
                return found.Problem;
            }

            user = found.User!;
            var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(LoginProvider, google.Subject, LoginProvider));
            if (!linked.Succeeded)
            {
                logger.LogError("Could not link a Google account: {Errors}", string.Join("; ", linked.Errors.Select(error => error.Description)));
                return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The Google account could not be linked.");
            }
        }

        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: LoginProvider);
        return TypedResults.Empty;
    }

    private sealed record FoundUser(AppUser? User, ProblemHttpResult? Problem);

    private static async Task<FoundUser> FindOrCreateUserAsync(
        GoogleIdentity google,
        AppDbContext db,
        UserManager<AppUser> userManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // 1. A login (from an earlier email + phone sign-in) with the same email.
        var normalizedEmail = userManager.NormalizeEmail(google.Email);
        var users = await userManager.Users
            .Where(candidate => candidate.NormalizedEmail == normalizedEmail)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (users.Count > 1)
        {
            return new FoundUser(null, Ambiguous());
        }

        if (users.Count == 1)
        {
            return new FoundUser(users[0], null);
        }

        // 2. A customer or contractor with that email (e.g. added by staff): their login, or a new one for them.
        var email = google.Email.Trim().ToLowerInvariant();
        var customerIds = await db.Customers
            .Where(customer => customer.Email != null && customer.Email.ToLower() == email)
            .Select(customer => customer.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        var contractorIds = await db.Contractors
            .Where(contractor => contractor.Email != null && contractor.Email.ToLower() == email)
            .Select(contractor => contractor.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (customerIds.Count > 1 || contractorIds.Count > 1)
        {
            return new FoundUser(null, Ambiguous());
        }

        Guid? customerId = customerIds.Count == 1 ? customerIds[0] : null;
        Guid? contractorId = contractorIds.Count == 1 ? contractorIds[0] : null;

        if (customerId is not null || contractorId is not null)
        {
            var linkedUsers = await userManager.Users
                .Where(candidate => (customerId != null && candidate.CustomerId == customerId) ||
                                    (contractorId != null && candidate.ContractorId == contractorId))
                .Take(2)
                .ToListAsync(cancellationToken);
            if (linkedUsers.Count > 1)
            {
                return new FoundUser(null, Ambiguous());
            }

            if (linkedUsers.Count == 1)
            {
                return new FoundUser(linkedUsers[0], null);
            }
        }
        else
        {
            // 3. Nobody with this email yet: a new customer signs up (no phone; staff can add one).
            var customer = new Customer { Name = google.Name ?? google.Email, Email = google.Email };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(cancellationToken);
            customerId = customer.Id;
            logger.LogInformation("A new customer signed up with Google.");
        }

        var user = new AppUser
        {
            UserName = $"google-{google.Subject}",
            Email = google.Email,
            EmailConfirmed = true,
            DisplayName = google.Name,
            CustomerId = customerId,
            ContractorId = contractorId
        };
        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            logger.LogError("Could not create a login for a Google account: {Errors}", string.Join("; ", created.Errors.Select(error => error.Description)));
            return new FoundUser(null, TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The login could not be created."));
        }

        if (customerId is not null)
        {
            await userManager.AddToRoleAsync(user, AppRoles.Customer);
        }

        if (contractorId is not null)
        {
            await userManager.AddToRoleAsync(user, AppRoles.Contractor);
        }

        return new FoundUser(user, null);
    }

    private static ProblemHttpResult Ambiguous() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "This email belongs to more than one account.",
            detail: "GoogleEmailAmbiguous");
}

/// <summary>A verified Google account: its stable ID ("sub"), verified email and display name.</summary>
public sealed record GoogleIdentity(string Subject, string Email, string? Name);

/// <summary>
/// Checks a Google OAuth access token with Google: it must have been issued to our client ID, and the account's
/// email must be verified. The browser gets the token from Google's popup (Google Identity Services token client).
/// </summary>
public sealed class GoogleTokenValidator(HttpClient http, IOptions<GoogleOptions> options, ILogger<GoogleTokenValidator> logger)
{
    public async Task<GoogleIdentity?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ClientId))
        {
            logger.LogError("Sign in with Google is not configured (Google:ClientId).");
            return null;
        }

        try
        {
            // Who the token was issued to: it must be our client, so tokens minted for other apps are refused.
            var info = await http.GetFromJsonAsync<TokenInfo>(
                $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(accessToken)}", cancellationToken);
            if (info is null || info.Audience != options.Value.ClientId)
            {
                logger.LogWarning("Rejected a Google access token issued to another client.");
                return null;
            }

            using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(userInfoRequest, cancellationToken);
            response.EnsureSuccessStatusCode();
            var profile = await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken);

            if (profile is null || string.IsNullOrEmpty(profile.Subject) || profile.Subject != info.Subject ||
                string.IsNullOrEmpty(profile.Email) || profile.EmailVerified != true)
            {
                logger.LogWarning("Rejected a Google account without a verified email.");
                return null;
            }

            return new GoogleIdentity(profile.Subject, profile.Email, string.IsNullOrWhiteSpace(profile.Name) ? null : profile.Name.Trim());
        }
        catch (HttpRequestException exception)
        {
            // Google answers 400 for invalid or expired tokens.
            logger.LogWarning(exception, "Rejected a Google access token.");
            return null;
        }
    }

    private sealed record TokenInfo(
        [property: JsonPropertyName("aud")] string? Audience,
        [property: JsonPropertyName("sub")] string? Subject);

    private sealed record UserInfo(
        [property: JsonPropertyName("sub")] string? Subject,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified,
        [property: JsonPropertyName("name")] string? Name);
}

public sealed class GoogleOptions
{
    public const string SectionName = "Google";

    /// <summary>The OAuth client ID of the web frontend, e.g. "1234-abc.apps.googleusercontent.com".</summary>
    public string? ClientId { get; init; }
}

public sealed record GoogleTokenRequest
{
    /// <summary>The access token from Google's popup (Google Identity Services token client).</summary>
    [Required]
    [MaxLength(4096)]
    public required string AccessToken { get; init; }
}
