using Microsoft.AspNetCore.Identity;

/// <summary>A login: team members, and later contractors and customers using the portals.</summary>
public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }

    /// <summary>Follow the device's light/dark setting (the default); otherwise <see cref="DarkTheme"/> decides.</summary>
    public bool SystemTheme { get; set; } = true;

    /// <summary>The theme they picked when not following the device: dark unless they switch it off.</summary>
    public bool DarkTheme { get; set; } = true;

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? ContractorId { get; set; }

    public Contractor? Contractor { get; set; }
}
