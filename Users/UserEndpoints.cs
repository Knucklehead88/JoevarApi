using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>The customers' and contractors' own logins (everyone who signed up on the website), for staff to look up.</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/users", ListUsersAsync)
            .WithTags("Users")
            .RequireAuthorization(AuthPolicies.Staff)
            .WithName("ListUsers")
            .WithSummary("List the logins that aren't staff, newest first");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<UserResponse>>> ListUsersAsync(
        AppDbContext db,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var staffIds = from userRole in db.UserRoles
                       join role in db.Roles on userRole.RoleId equals role.Id
                       where StaffAccounts.Roles.Contains(role.Name!)
                       select userRole.UserId;
        var query = db.Users.AsNoTracking().Where(user => !staffIds.Contains(user.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            var pattern = $"%{text}%";
            query = query.Where(user =>
                (user.DisplayName != null && EF.Functions.ILike(user.DisplayName, pattern)) ||
                (user.Email != null && EF.Functions.ILike(user.Email, pattern)) ||
                EF.Functions.ILike(user.UserName!, pattern) ||
                (user.PhoneNumber != null && user.PhoneNumber.Contains(text)));
        }

        // Logins from before CreatedAt existed have none; they come last.
        var result = await query
            .OrderByDescending(user => user.CreatedAt.HasValue)
            .ThenByDescending(user => user.CreatedAt)
            .ThenBy(user => user.UserName)
            .Select(user => new
            {
                user.Id,
                user.UserName,
                user.DisplayName,
                user.Email,
                user.PhoneNumber,
                user.PhoneNumberConfirmed,
                HasPassword = user.PasswordHash != null,
                Providers = db.UserLogins.Where(login => login.UserId == user.Id).Select(login => login.LoginProvider).ToList(),
                user.ContractorId,
                user.CreatedAt
            })
            .ToPagedResultAsync(page, pageSize, cancellationToken);

        var items = result.Items.Select(user => new UserResponse(
            user.Id,
            user.UserName!,
            user.DisplayName,
            user.Email,
            user.PhoneNumber,
            user.ContractorId is null ? AppRoles.Customer : AppRoles.Contractor,
            [
                .. user.PhoneNumberConfirmed ? ["Phone"] : Array.Empty<string>(),
                .. user.HasPassword ? ["Password"] : Array.Empty<string>(),
                .. user.Providers.Order()
            ],
            user.CreatedAt)).ToList();
        return TypedResults.Ok(new PagedResult<UserResponse>(items, result.Total, result.Page, result.PageSize));
    }
}

/// <summary>A customer's or contractor's login. <see cref="SignInMethods"/>: Phone, Password, Google, Microsoft, Apple.</summary>
public sealed record UserResponse(
    string Id,
    string UserName,
    string? DisplayName,
    string? Email,
    string? PhoneNumber,
    string Kind,
    IReadOnlyList<string> SignInMethods,
    DateTimeOffset? CreatedAt);
