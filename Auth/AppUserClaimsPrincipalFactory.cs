using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

/// <summary>
/// Adds app claims to every token, including refreshed ones: whether two-factor sign-in is enabled
/// (required for staff) and which customer/contractor the login belongs to.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(userManager, roleManager, options)
{
    public const string TwoFactorEnabledClaim = "two_factor_enabled";
    public const string CustomerIdClaim = "customer_id";
    public const string ContractorIdClaim = "contractor_id";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.TwoFactorEnabled)
        {
            identity.AddClaim(new Claim(TwoFactorEnabledClaim, "true"));
        }

        if (user.CustomerId is not null)
        {
            identity.AddClaim(new Claim(CustomerIdClaim, user.CustomerId.Value.ToString()));
        }

        if (user.ContractorId is not null)
        {
            identity.AddClaim(new Claim(ContractorIdClaim, user.ContractorId.Value.ToString()));
        }

        return identity;
    }
}
