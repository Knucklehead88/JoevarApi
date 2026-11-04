using System.Security.Claims;

/// <summary>Who sees which customer projects: the office sees them all, Crew only those they own, contribute to or are assigned on.</summary>
public static class ProjectAccess
{
    public static bool IsOffice(ClaimsPrincipal principal) => StaffAccounts.OfficeRoles.Any(principal.IsInRole);

    public static string UserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public static IQueryable<Project> Visible(AppDbContext db, ClaimsPrincipal principal) =>
        Visible(db, UserId(principal), IsOffice(principal));

    public static IQueryable<Project> Visible(AppDbContext db, string userId, bool office) =>
        office
            ? db.Projects
            : db.Projects.Where(project =>
                project.OwnerId == userId
                || project.Contributors.Any(user => user.Id == userId)
                || project.Items.Any(item => item.Assignees.Any(user => user.Id == userId)));

    /// <summary>The logins that can own, contribute to or be assigned on a project: staff and Crew, not blocked.</summary>
    public static IQueryable<AppUser> TeamLogins(AppDbContext db)
    {
        var staffIds = from userRole in db.UserRoles
                       join role in db.Roles on userRole.RoleId equals role.Id
                       where StaffAccounts.Roles.Contains(role.Name!)
                       select userRole.UserId;
        return db.Users.Where(user => staffIds.Contains(user.Id) && (user.LockoutEnd == null || user.LockoutEnd < StaffAccounts.BlockedUntil));
    }
}
