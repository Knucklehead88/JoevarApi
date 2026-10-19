using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

/// <summary>Setup link for authenticator apps (Microsoft Authenticator, Google Authenticator, ...).</summary>
public static class AuthenticatorEndpoints
{
    private const string Issuer = "Joevar";

    public static IEndpointRouteBuilder MapAuthenticatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/manage/authenticator", GetSetupAsync)
            .RequireAuthorization()
            .WithTags("Auth")
            .WithName("GetAuthenticatorSetup")
            .WithSummary("Get the otpauth:// URI to show as a QR code for an authenticator app");

        return endpoints;
    }

    private static async Task<Results<Ok<AuthenticatorSetupResponse>, UnauthorizedHttpResult>> GetSetupAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        var account = Uri.EscapeDataString(user.UserName!);
        var uri = $"otpauth://totp/{Issuer}:{account}?secret={key}&issuer={Issuer}&digits=6";
        return TypedResults.Ok(new AuthenticatorSetupResponse(key!, uri, user.TwoFactorEnabled));
    }
}

public sealed record AuthenticatorSetupResponse(string SharedKey, string AuthenticatorUri, bool IsTwoFactorEnabled);
