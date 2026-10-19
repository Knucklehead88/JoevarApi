using Microsoft.AspNetCore.Identity;

/// <summary>One-time bootstrap: <c>dotnet run -- create-admin &lt;username&gt; &lt;password&gt;</c>.</summary>
public static class AdminSetup
{
    public static async Task<int> CreateAdminAsync(IServiceProvider services, string userName, string password)
    {
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        if (await userManager.FindByNameAsync(userName) is not null)
        {
            Console.Error.WriteLine($"User '{userName}' already exists.");
            return 1;
        }

        var user = new AppUser { UserName = userName, DisplayName = userName };
        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            result = await userManager.AddToRoleAsync(user, AppRoles.Admin);
        }

        if (!result.Succeeded)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, result.Errors.Select(error => error.Description)));
            return 1;
        }

        Console.WriteLine($"Admin '{userName}' created. Sign in, then set up the authenticator app to reach staff endpoints.");
        return 0;
    }
}
