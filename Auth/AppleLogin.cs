using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Sign in with Apple. Apple gives no phone number, so an Apple ID must first be linked by a user who signed in
/// with their phone code; after that it signs straight in.
/// </summary>
public static class AppleLoginEndpoints
{
    public const string LoginProvider = "Apple";

    public static IEndpointRouteBuilder MapAppleLoginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth/apple").WithTags("Auth");

        group.MapPost("/", LoginAsync)
            .RequireRateLimiting(AuthRateLimits.Policy)
            .WithName("AppleLogin")
            .WithSummary("Exchange an Apple identity token for a bearer token");
        group.MapPost("/link", LinkAsync)
            .RequireAuthorization()
            .WithName("AppleLink")
            .WithSummary("Link an Apple ID to the signed-in user");

        return endpoints;
    }

    private static async Task<Results<EmptyHttpResult, UnauthorizedHttpResult, ProblemHttpResult>> LoginAsync(
        AppleTokenRequest request,
        AppleTokenValidator validator,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        CancellationToken cancellationToken)
    {
        var appleUserId = await validator.ValidateAsync(request.IdentityToken, cancellationToken);
        if (appleUserId is null)
        {
            return TypedResults.Unauthorized();
        }

        var user = await userManager.FindByLoginAsync(LoginProvider, appleUserId);
        if (user is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "This Apple ID is not linked yet.",
                detail: "Sign in with your phone number once, then link your Apple ID.");
        }

        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: LoginProvider);
        return TypedResults.Empty;
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, Conflict<string>>> LinkAsync(
        AppleTokenRequest request,
        ClaimsPrincipal principal,
        AppleTokenValidator validator,
        UserManager<AppUser> userManager,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(principal);
        var appleUserId = await validator.ValidateAsync(request.IdentityToken, cancellationToken);
        if (user is null || appleUserId is null)
        {
            return TypedResults.Unauthorized();
        }

        var linkedUser = await userManager.FindByLoginAsync(LoginProvider, appleUserId);
        if (linkedUser is not null)
        {
            return linkedUser.Id == user.Id
                ? TypedResults.NoContent()
                : TypedResults.Conflict("This Apple ID is already linked to another account.");
        }

        var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(LoginProvider, appleUserId, LoginProvider));
        return linked.Succeeded ? TypedResults.NoContent() : TypedResults.Conflict("The Apple ID could not be linked.");
    }
}

/// <summary>Validates Apple identity tokens (JWTs) against Apple's published signing keys.</summary>
public sealed class AppleTokenValidator(IOptions<AppleOptions> options, ILogger<AppleTokenValidator> logger)
{
    private const string Issuer = "https://appleid.apple.com";

    private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration = new(
        $"{Issuer}/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever());

    /// <summary>Returns Apple's stable user ID ("sub") for a valid token, or null.</summary>
    public async Task<string?> ValidateAsync(string identityToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ClientId))
        {
            logger.LogError("Sign in with Apple is not configured (Apple:ClientId).");
            return null;
        }

        var openIdConfiguration = await configuration.GetConfigurationAsync(cancellationToken);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(identityToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = options.Value.ClientId,
            IssuerSigningKeys = openIdConfiguration.SigningKeys,
            ValidateLifetime = true
        });

        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "Rejected an Apple identity token.");
            return null;
        }

        return result.ClaimsIdentity.FindFirst("sub")?.Value;
    }
}

public sealed class AppleOptions
{
    public const string SectionName = "Apple";

    /// <summary>The Apple Services ID used by the web frontend, e.g. "com.joevar.web".</summary>
    public string? ClientId { get; init; }
}

public sealed record AppleTokenRequest
{
    /// <summary>The id_token returned by Sign in with Apple (Apple JS).</summary>
    [Required]
    public required string IdentityToken { get; init; }
}
