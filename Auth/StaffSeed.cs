using Microsoft.AspNetCore.Identity;

/// <summary>
/// Development only: on startup, creates Joevar's staff logins (Admins) that don't exist yet, all with the password in
/// the <c>Seed:StaffPassword</c> user secret (never in the repo). Existing users are left alone, so a changed password
/// isn't reset. Each still sets up the authenticator app at first sign-in.
/// </summary>
public static class StaffSeed
{
    /// <summary>User names and display names.</summary>
    private static readonly (string UserName, string DisplayName)[] Staff =
    [
        ("joe", "Joe"),
        ("cristi", "Cristi"),
        ("silviu", "Silviu"),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StaffSeed");
        var password = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Seed:StaffPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation("Staff seed skipped: set the Seed:StaffPassword user secret to create the staff logins.");
            return;
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        try
        {
            foreach (var (userName, displayName) in Staff)
            {
                if (await userManager.FindByNameAsync(userName) is not null)
                {
                    continue;
                }

                var user = new AppUser { UserName = userName, DisplayName = displayName };
                var result = await userManager.CreateAsync(user, password);
                if (result.Succeeded)
                {
                    result = await userManager.AddToRoleAsync(user, AppRoles.Admin);
                }

                if (result.Succeeded)
                {
                    logger.LogInformation("Seeded staff login {UserName}.", userName);
                }
                else
                {
                    logger.LogWarning(
                        "Couldn't seed staff login {UserName}: {Errors}",
                        userName,
                        string.Join(" ", result.Errors.Select(error => error.Description)));
                }
            }
        }
        catch (Exception exception)
        {
            // Typically a database that isn't migrated yet; the API still starts.
            logger.LogWarning(exception, "Staff seed failed; run dotnet ef database update and restart.");
        }
    }
}
