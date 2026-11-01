using Microsoft.AspNetCore.Identity;

/// <summary>A login: team members, and later contractors and customers using the portals.</summary>
public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }

    /// <summary>The theme in their profile: dark unless they switch it off.</summary>
    public bool DarkTheme { get; set; } = true;

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? ContractorId { get; set; }

    public Contractor? Contractor { get; set; }
}
