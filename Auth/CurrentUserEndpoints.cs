using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

public static class CurrentUserEndpoints
{
    public static IEndpointRouteBuilder MapCurrentUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/auth/me", GetCurrentUserAsync)
            .RequireAuthorization()
            .WithTags("Auth")
            .WithName("GetCurrentUser")
            .WithSummary("Get the signed-in user's roles, links and sign-in methods");

        return endpoints;
    }

    private static async Task<Results<Ok<CurrentUserResponse>, UnauthorizedHttpResult>> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var roles = await userManager.GetRolesAsync(user);
        var passkeys = await userManager.GetPasskeysAsync(user);
        var logins = await userManager.GetLoginsAsync(user);

        return TypedResults.Ok(new CurrentUserResponse(
            user.Id,
            user.UserName!,
            user.DisplayName,
            user.Email,
            user.PhoneNumber,
            roles.Order().ToList(),
            user.CustomerId,
            user.ContractorId,
            user.TwoFactorEnabled,
            passkeys.Count,
            logins.Any(login => login.LoginProvider == AppleLoginEndpoints.LoginProvider)));
    }
}

/// <summary>
/// Who is signed in. Staff with <see cref="IsTwoFactorEnabled"/> false must set up the authenticator app
/// before staff endpoints will answer.
/// </summary>
public sealed record CurrentUserResponse(
    string Id,
    string UserName,
    string? DisplayName,
    string? Email,
    string? PhoneNumber,
    IReadOnlyList<string> Roles,
    Guid? CustomerId,
    Guid? ContractorId,
    bool IsTwoFactorEnabled,
    int PasskeyCount,
    bool IsAppleLinked);
