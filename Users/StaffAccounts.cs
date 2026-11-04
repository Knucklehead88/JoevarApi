using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Staff logins and blocking. A blocked login is locked out until <see cref="BlockedUntil"/> (Identity's lockout, so
/// sign-in answers "LockedOut"), its security stamp is renewed (so refresh tokens stop working), and
/// <see cref="UseBlockedLoginCheck"/> treats its access tokens as signed out straight away.
/// </summary>
public static class StaffAccounts
{
    public static readonly string[] Roles = [AppRoles.Admin, AppRoles.Manager, AppRoles.VirtualAssistant];

    /// <summary>
    /// Far enough ahead to mean "until unblocked", and unlike <see cref="DateTimeOffset.MaxValue"/> stored as is by
    /// PostgreSQL. Lockouts after wrong passwords last minutes, so anything this late is a block.
    /// </summary>
    public static readonly DateTimeOffset BlockedUntil = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsBlocked(AppUser user) => user.LockoutEnd >= BlockedUntil;

    /// <summary>
    /// Requests from a blocked or deleted login continue as signed out, so endpoints that need a login answer 401 and
    /// the frontend signs out (its refresh fails too). Without it an access token would keep working for up to an hour.
    /// </summary>
    public static IApplicationBuilder UseBlockedLoginCheck(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            var active = userId is not null && await db.Users.AnyAsync(
                user => user.Id == userId && (user.LockoutEnd == null || user.LockoutEnd < BlockedUntil),
                context.RequestAborted);
            if (!active)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }

        await next(context);
    });
}
