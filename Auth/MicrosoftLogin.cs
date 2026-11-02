using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Sign in (and sign up) with a Microsoft account, personal (Outlook.com, Hotmail, Live) or work/school, for customers
/// and contractors: the login already linked to it, else one found or created by <see cref="ExternalAccounts"/>.
/// </summary>
public static class MicrosoftLoginEndpoints
{
    public const string LoginProvider = "Microsoft";

    public static IEndpointRouteBuilder MapMicrosoftLoginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/microsoft", LoginAsync)
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy)
            .Produces<AccessTokenResponse>()
            .WithName("MicrosoftLogin")
            .WithSummary("Exchange a Microsoft ID token for a bearer token, signing up a new customer if needed");

        return endpoints;
    }

    private static async Task<Results<EmptyHttpResult, UnauthorizedHttpResult, ProblemHttpResult>> LoginAsync(
        MicrosoftTokenRequest request,
        MicrosoftTokenValidator validator,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var identity = await validator.ValidateAsync(request.IdToken, cancellationToken);
        if (identity is null)
        {
            return TypedResults.Unauthorized();
        }

        return await ExternalAccounts.SignInAsync(
            identity, db, userManager, signInManager, loggerFactory.CreateLogger("MicrosoftLogin"), cancellationToken);
    }
}

/// <summary>
/// Checks a Microsoft identity platform (v2.0) ID token: signed by Microsoft, issued to our client ID, not expired, and
/// issued by the account's own tenant. Only personal accounts' emails count as verified: a work/school account's email
/// is whatever its organization's admin set.
/// </summary>
public sealed class MicrosoftTokenValidator(IOptions<MicrosoftOptions> options, ILogger<MicrosoftTokenValidator> logger)
{
    /// <summary>The tenant every personal Microsoft account (MSA) signs in through.</summary>
    private const string PersonalAccountsTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration = new(
        "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever());

    public async Task<ExternalAccounts.ExternalIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ClientId))
        {
            logger.LogError("Sign in with Microsoft is not configured (Microsoft:ClientId).");
            return null;
        }

        var openIdConfiguration = await configuration.GetConfigurationAsync(cancellationToken);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidAudience = options.Value.ClientId,
            IssuerSigningKeys = openIdConfiguration.SigningKeys,
            ValidateLifetime = true,
            // Every tenant has its own issuer; it must be the one of the tenant in the token ("tid").
            IssuerValidator = (issuer, token, _) =>
            {
                var tenant = (token as JsonWebToken)?.GetPayloadValue<string>("tid");
                return tenant is not null && issuer == $"https://login.microsoftonline.com/{tenant}/v2.0"
                    ? issuer
                    : throw new SecurityTokenInvalidIssuerException("The issuer doesn't match the token's tenant.");
            }
        });

        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "Rejected a Microsoft ID token.");
            return null;
        }

        var claims = result.ClaimsIdentity;
        var subject = claims.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var personal = claims.FindFirst("tid")?.Value == PersonalAccountsTenant;
        // Personal accounts sign in with their email, which Microsoft has verified (preferred_username).
        var email = claims.FindFirst("email")?.Value ?? (personal ? claims.FindFirst("preferred_username")?.Value : null);
        if (email is not null && !new EmailAddressAttribute().IsValid(email))
        {
            email = null;
        }

        var name = claims.FindFirst("name")?.Value;
        return new ExternalAccounts.ExternalIdentity(
            MicrosoftLoginEndpoints.LoginProvider,
            subject,
            email,
            EmailVerified: personal && email is not null,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }
}

public sealed class MicrosoftOptions
{
    public const string SectionName = "Microsoft";

    /// <summary>The app registration's Application (client) ID, a GUID.</summary>
    public string? ClientId { get; init; }
}

public sealed record MicrosoftTokenRequest
{
    /// <summary>The ID token from Microsoft's sign-in popup (MSAL.js).</summary>
    [Required]
    [MaxLength(8192)]
    public required string IdToken { get; init; }
}
