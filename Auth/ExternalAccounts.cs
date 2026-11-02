using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Finds or creates the login for a Google or Microsoft account seen for the first time. One account per person: a
/// <b>verified</b> email is matched, in order, to a login whose email is also verified, then a customer/contractor with
/// that email (whose login is created), and only then a new customer is created. An unverified email (a work/school
/// Microsoft account, whose admin can set any address) never joins an existing account, or someone could take it over.
/// </summary>
public static class ExternalAccounts
{
    public sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified, string? Name);

    public sealed record FoundUser(AppUser? User, ProblemHttpResult? Problem);

    public static async Task<FoundUser> FindOrCreateUserAsync(
        ExternalIdentity identity,
        AppDbContext db,
        UserManager<AppUser> userManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Guid? customerId = null;
        Guid? contractorId = null;

        if (identity is { EmailVerified: true, Email: { } verifiedEmail })
        {
            // 1. A login with the same, verified email.
            var normalizedEmail = userManager.NormalizeEmail(verifiedEmail);
            var users = await userManager.Users
                .Where(candidate => candidate.NormalizedEmail == normalizedEmail && candidate.EmailConfirmed)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (users.Count > 1)
            {
                return new FoundUser(null, Ambiguous(identity.Provider));
            }

            if (users.Count == 1)
            {
                return new FoundUser(users[0], null);
            }

            // 2. A customer or contractor with that email (e.g. added by staff): their login, or a new one for them.
            var email = verifiedEmail.Trim().ToLowerInvariant();
            var customerIds = await db.Customers
                .Where(customer => customer.Email != null && customer.Email.ToLower() == email)
                .Select(customer => customer.Id)
                .Take(2)
                .ToListAsync(cancellationToken);
            var contractorIds = await db.Contractors
                .Where(contractor => contractor.Email != null && contractor.Email.ToLower() == email)
                .Select(contractor => contractor.Id)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (customerIds.Count > 1 || contractorIds.Count > 1)
            {
                return new FoundUser(null, Ambiguous(identity.Provider));
            }

            customerId = customerIds.Count == 1 ? customerIds[0] : null;
            contractorId = contractorIds.Count == 1 ? contractorIds[0] : null;

            if (customerId is not null || contractorId is not null)
            {
                var linkedUsers = await userManager.Users
                    .Where(candidate => (customerId != null && candidate.CustomerId == customerId) ||
                                        (contractorId != null && candidate.ContractorId == contractorId))
                    .Take(2)
                    .ToListAsync(cancellationToken);
                if (linkedUsers.Count > 1)
                {
                    return new FoundUser(null, Ambiguous(identity.Provider));
                }

                if (linkedUsers.Count == 1)
                {
                    return new FoundUser(linkedUsers[0], null);
                }
            }
        }
        else if (identity.Email is { } unverifiedEmail && await EmailOnFileAsync(unverifiedEmail, db, userManager, cancellationToken))
        {
            return new FoundUser(null, TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "This email is already used by another account.",
                detail: $"{identity.Provider}EmailInUse"));
        }

        if (customerId is null && contractorId is null)
        {
            // 3. Nobody with this email yet: a new customer signs up (no phone; staff can add one). An unverified email
            // stays off the customer record, so it can't make a later sign-in with that email land here.
            var customer = new Customer
            {
                Name = identity.Name ?? identity.Email ?? $"{identity.Provider} user",
                Email = identity.EmailVerified ? identity.Email : null
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(cancellationToken);
            customerId = customer.Id;
            logger.LogInformation("A new customer signed up with {Provider}.", identity.Provider);
        }

        var user = new AppUser
        {
            UserName = $"{identity.Provider.ToLowerInvariant()}-{identity.Subject}",
            Email = identity.Email,
            EmailConfirmed = identity.EmailVerified && identity.Email is not null,
            DisplayName = identity.Name,
            CustomerId = customerId,
            ContractorId = contractorId
        };
        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            logger.LogError("Could not create a login for a {Provider} account: {Errors}", identity.Provider, string.Join("; ", created.Errors.Select(error => error.Description)));
            return new FoundUser(null, TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The login could not be created."));
        }

        if (customerId is not null)
        {
            await userManager.AddToRoleAsync(user, AppRoles.Customer);
        }

        if (contractorId is not null)
        {
            await userManager.AddToRoleAsync(user, AppRoles.Contractor);
        }

        return new FoundUser(user, null);
    }

    /// <summary>Any login, customer or contractor with this email, verified or not (emails ignore case).</summary>
    public static async Task<bool> EmailOnFileAsync(string email, AppDbContext db, UserManager<AppUser> userManager, CancellationToken cancellationToken)
    {
        var normalizedEmail = userManager.NormalizeEmail(email);
        var lower = email.Trim().ToLowerInvariant();
        return await userManager.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken) ||
               await db.Customers.AnyAsync(customer => customer.Email != null && customer.Email.ToLower() == lower, cancellationToken) ||
               await db.Contractors.AnyAsync(contractor => contractor.Email != null && contractor.Email.ToLower() == lower, cancellationToken);
    }

    /// <summary>Links the account to the login and signs in with a bearer token, after the first-time lookup.</summary>
    public static async Task<Results<EmptyHttpResult, UnauthorizedHttpResult, ProblemHttpResult>> SignInAsync(
        ExternalIdentity identity,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByLoginAsync(identity.Provider, identity.Subject);
        if (user is null)
        {
            var found = await FindOrCreateUserAsync(identity, db, userManager, logger, cancellationToken);
            if (found.Problem is not null)
            {
                return found.Problem;
            }

            user = found.User!;
            var linked = await userManager.AddLoginAsync(user, new UserLoginInfo(identity.Provider, identity.Subject, identity.Provider));
            if (!linked.Succeeded)
            {
                logger.LogError("Could not link a {Provider} account: {Errors}", identity.Provider, string.Join("; ", linked.Errors.Select(error => error.Description)));
                return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: $"The {identity.Provider} account could not be linked.");
            }
        }

        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: identity.Provider);
        return TypedResults.Empty;
    }

    private static ProblemHttpResult Ambiguous(string provider) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "This email belongs to more than one account.",
            detail: $"{provider}EmailAmbiguous");
}
