/// <summary>A contractor's registration from the website, for staff to review before adding them as a contractor.</summary>
public sealed class ContractorApplication
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string FullName { get; set; }

    public string? CompanyName { get; set; }

    /// <summary>Their trades (the same values as a contractor's trades).</summary>
    public List<ProjectType> Professions { get; set; } = [];

    /// <summary>A US state's full name, e.g. "Illinois".</summary>
    public required string State { get; set; }

    public required string City { get; set; }

    /// <summary>Street and number, e.g. "1114 Circle Ave".</summary>
    public required string StreetAddress { get; set; }

    /// <summary>A US ZIP code: "60130" or "60130-1234".</summary>
    public required string ZipCode { get; set; }

    /// <summary>Licenses they hold, from <see cref="ContractorApplicationEndpoints.Licenses"/>.</summary>
    public List<string> Licenses { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
