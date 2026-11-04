using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Admins manage the staff logins: list, add (they set up the authenticator at first sign-in), block, unblock and
/// delete. Nobody can block or delete themselves, and the last active Admin can't be blocked or deleted.
/// </summary>
public static class StaffEndpoints
{
    public static IEndpointRouteBuilder MapStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/staff")
            .WithTags("Staff")
            .RequireAuthorization(AuthPolicies.Admin);

        group.MapGet("/", ListStaffAsync).WithName("ListStaff").WithSummary("List the staff logins");
        group.MapPost("/", CreateStaffAsync).WithName("CreateStaff").WithSummary("Add a staff login");
        group.MapPost("/{id}/block", BlockStaffAsync).WithName("BlockStaff").WithSummary("Block a staff login and sign it out");
        group.MapPost("/{id}/unblock", UnblockStaffAsync).WithName("UnblockStaff").WithSummary("Let a blocked staff login sign in again");
        group.MapDelete("/{id}", DeleteStaffAsync).WithName("DeleteStaff").WithSummary("Delete a staff login");

        return endpoints;
    }

    private static async Task<Ok<List<StaffResponse>>> ListStaffAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await (from user in db.Users.AsNoTracking()
                          join userRole in db.UserRoles on user.Id equals userRole.UserId
                          join role in db.Roles on userRole.RoleId equals role.Id
                          where StaffAccounts.Roles.Contains(role.Name!)
                          select new { User = user, Role = role.Name! })
            .ToListAsync(cancellationToken);

        var me = userManager.GetUserId(principal);
        var staff = rows
            .GroupBy(row => row.User.Id)
            .Select(logins => ToResponse(logins.First().User, HighestRole(logins.Select(row => row.Role)), me))
            .OrderBy(member => member.DisplayName ?? member.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok(staff);
    }

    private static async Task<Results<Created<StaffResponse>, ValidationProblem>> CreateStaffAsync(
        CreateStaffRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        if (!StaffAccounts.Roles.Contains(request.Role))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = [$"Choose one of {string.Join(", ", StaffAccounts.Roles)}."]
            });
        }

        var user = new AppUser
        {
            UserName = request.UserName.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim()
        };
        var result = await userManager.CreateAsync(user, request.Password);
        if (result.Succeeded)
        {
            result = await userManager.AddToRoleAsync(user, request.Role);
        }

        if (!result.Succeeded)
        {
            // Identity's error codes say which field is wrong: PasswordTooShort, DuplicateUserName, InvalidEmail, ...
            var errors = result.Errors
                .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password"
                    : error.Code.Contains("Email", StringComparison.Ordinal) ? "email"
                    : error.Code.Contains("UserName", StringComparison.Ordinal) ? "userName"
                    : "role")
                .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
            if (await userManager.FindByIdAsync(user.Id) is { } created)
            {
                await userManager.DeleteAsync(created);
            }

            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Created($"/api/staff/{user.Id}", ToResponse(user, request.Role, userManager.GetUserId(principal)));
    }

    private static async Task<Results<Ok<StaffResponse>, NotFound, Conflict<string>>> BlockStaffAsync(
        string id,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var (user, role, conflict) = await FindChangeableAsync(id, principal, userManager, "block");
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (conflict is not null)
        {
            return TypedResults.Conflict(conflict);
        }

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, StaffAccounts.BlockedUntil);
        // Refresh tokens carry the old stamp, so they stop working.
        await userManager.UpdateSecurityStampAsync(user);
        return TypedResults.Ok(ToResponse(user, role!, userManager.GetUserId(principal)));
    }

    private static async Task<Results<Ok<StaffResponse>, NotFound>> UnblockStaffAsync(
        string id,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByIdAsync(id);
        var role = user is null ? null : StaffRole(await userManager.GetRolesAsync(user));
        if (user is null || role is null)
        {
            return TypedResults.NotFound();
        }

        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        return TypedResults.Ok(ToResponse(user, role, userManager.GetUserId(principal)));
    }

    private static async Task<Results<NoContent, NotFound, Conflict<string>>> DeleteStaffAsync(
        string id,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var (user, _, conflict) = await FindChangeableAsync(id, principal, userManager, "delete");
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (conflict is not null)
        {
            return TypedResults.Conflict(conflict);
        }

        await userManager.DeleteAsync(user);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The staff login to block or delete (null when there's no such staff login), and why it can't be when it's
    /// yourself or the last active Admin.
    /// </summary>
    private static async Task<(AppUser? User, string? Role, string? Conflict)> FindChangeableAsync(
        string id,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        string action)
    {
        var user = await userManager.FindByIdAsync(id);
        var role = user is null ? null : StaffRole(await userManager.GetRolesAsync(user));
        if (user is null || role is null)
        {
            return (null, null, null);
        }

        if (user.Id == userManager.GetUserId(principal))
        {
            return (user, role, $"You can't {action} your own login.");
        }

        if (role == AppRoles.Admin)
        {
            var admins = await userManager.GetUsersInRoleAsync(AppRoles.Admin);
            if (!admins.Any(admin => admin.Id != user.Id && !StaffAccounts.IsBlocked(admin)))
            {
                return (user, role, $"This is the last active Admin; add another Admin before you {action} it.");
            }
        }

        return (user, role, null);
    }

    private static string? StaffRole(IEnumerable<string> roles)
    {
        var staffRoles = roles.Where(StaffAccounts.Roles.Contains).ToList();
        return staffRoles.Count == 0 ? null : HighestRole(staffRoles);
    }

    /// <summary>Admin, else Manager, else VA (<see cref="StaffAccounts.Roles"/> is in that order).</summary>
    private static string HighestRole(IEnumerable<string> roles) =>
        StaffAccounts.Roles.First(roles.Contains);

    private static StaffResponse ToResponse(AppUser user, string role, string? me) => new(
        user.Id,
        user.UserName!,
        user.DisplayName,
        user.Email,
        role,
        user.TwoFactorEnabled,
        StaffAccounts.IsBlocked(user),
        user.CreatedAt,
        user.Id == me);
}

public sealed record CreateStaffRequest
{
    [Required]
    [MaxLength(256)]
    public required string UserName { get; init; }

    [Required]
    [MaxLength(200)]
    public required string DisplayName { get; init; }

    [EmailAddress]
    [MaxLength(256)]
    public string? Email { get; init; }

    /// <summary>Admin, Manager or VA.</summary>
    [Required]
    public required string Role { get; init; }

    /// <summary>The first password; they sign in with it and set up the authenticator.</summary>
    [Required]
    [MaxLength(200)]
    public required string Password { get; init; }
}

/// <summary>A staff login. <see cref="IsYou"/> marks the Admin asking, who can't block or delete themselves.</summary>
public sealed record StaffResponse(
    string Id,
    string UserName,
    string? DisplayName,
    string? Email,
    string Role,
    bool IsTwoFactorEnabled,
    bool IsBlocked,
    DateTimeOffset? CreatedAt,
    bool IsYou);
