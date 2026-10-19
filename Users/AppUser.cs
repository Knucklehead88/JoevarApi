using Microsoft.AspNetCore.Identity;

/// <summary>A login: team members, and later contractors and customers using the portals.</summary>
public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? ContractorId { get; set; }

    public Contractor? Contractor { get; set; }
}
