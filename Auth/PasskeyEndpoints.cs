using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

/// <summary>
/// Passkeys (Face ID, Touch ID, Windows Hello). A signed-in user registers one, then signs in with it without a code.
/// Each options call stores a short-lived challenge cookie, so the frontend must send requests with credentials.
/// </summary>
public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth/passkey").WithTags("Auth");

        group.MapPost("/register/options", RegisterOptionsAsync)
            .RequireAuthorization()
            .WithName("PasskeyRegisterOptions")
            .WithSummary("Get options for navigator.credentials.create()");
        group.MapPost("/register", RegisterAsync)
            .RequireAuthorization()
            .WithName("PasskeyRegister")
            .WithSummary("Save a new passkey for the signed-in user");
        group.MapPost("/login/options", LoginOptionsAsync)
            .RequireRateLimiting(AuthRateLimits.Policy)
            .WithName("PasskeyLoginOptions")
            .WithSummary("Get options for navigator.credentials.get()");
        group.MapPost("/login", LoginAsync)
            .RequireRateLimiting(AuthRateLimits.Policy)
            .WithName("PasskeyLogin")
            .WithSummary("Exchange a passkey assertion for a bearer token");

        return endpoints;
    }

    private static async Task<Results<ContentHttpResult, UnauthorizedHttpResult>> RegisterOptionsAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var options = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = user.Id,
            Name = user.UserName!,
            DisplayName = user.DisplayName ?? user.UserName!
        });
        return TypedResults.Content(options, "application/json");
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, ValidationProblem>> RegisterAsync(
        PasskeyRegisterRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var attestation = await signInManager.PerformPasskeyAttestationAsync(request.Credential.GetRawText());
        if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id)
        {
            return PasskeyProblem(attestation.Failure?.Message ?? "The passkey was created for a different user.");
        }

        var passkey = attestation.Passkey;
        passkey.Name = request.Name?.Trim();
        var saved = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
        return saved.Succeeded
            ? TypedResults.NoContent()
            : PasskeyProblem(string.Join("; ", saved.Errors.Select(error => error.Description)));
    }

    private static async Task<ContentHttpResult> LoginOptionsAsync(SignInManager<AppUser> signInManager)
    {
        // No user: the browser offers any passkey it has for this site (discoverable credentials).
        var options = await signInManager.MakePasskeyRequestOptionsAsync(null);
        return TypedResults.Content(options, "application/json");
    }

    private static async Task<Results<EmptyHttpResult, UnauthorizedHttpResult>> LoginAsync(
        PasskeyLoginRequest request,
        SignInManager<AppUser> signInManager)
    {
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager.PasskeySignInAsync(request.Credential.GetRawText());
        return result.Succeeded ? TypedResults.Empty : TypedResults.Unauthorized();
    }

    private static ValidationProblem PasskeyProblem(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = [message] });
}

/// <summary>The JSON-serialized result of navigator.credentials.create(), plus an optional label like "Norman's iPhone".</summary>
public sealed record PasskeyRegisterRequest
{
    [Required]
    public required JsonElement Credential { get; init; }

    [MaxLength(100)]
    public string? Name { get; init; }
}

/// <summary>The JSON-serialized result of navigator.credentials.get().</summary>
public sealed record PasskeyLoginRequest
{
    [Required]
    public required JsonElement Credential { get; init; }
}
