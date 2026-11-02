using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Sign in (and sign up) with Google for customers and contractors: the login already linked to the Google account,
/// else one found or created by <see cref="ExternalAccounts"/> (one account per person).
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
        var google = await validator.ValidateAsync(request.AccessToken, cancellationToken);
        if (google is null)
        {
            return TypedResults.Unauthorized();
        }

        // Google only gives us verified emails (see GoogleTokenValidator).
        return await ExternalAccounts.SignInAsync(
            new ExternalAccounts.ExternalIdentity(LoginProvider, google.Subject, google.Email, EmailVerified: true, google.Name),
            db, userManager, signInManager, loggerFactory.CreateLogger("GoogleLogin"), cancellationToken);
    }
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
