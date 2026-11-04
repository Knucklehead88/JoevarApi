using Microsoft.AspNetCore.Identity;

/// <summary>A login: team members, and later contractors and customers using the portals.</summary>
public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }

    /// <summary>When the login was created; empty for logins made before 2026-11-04, when this was added.</summary>
    public DateTimeOffset? CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Follow the device's light/dark setting (the default); otherwise <see cref="DarkTheme"/> decides.</summary>
    public bool SystemTheme { get; set; } = true;

    /// <summary>The theme they picked when not following the device: dark unless they switch it off.</summary>
    public bool DarkTheme { get; set; } = true;

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? ContractorId { get; set; }

    public Contractor? Contractor { get; set; }
}
