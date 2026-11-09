/// <summary>
/// A bathroom project request from the Bathroom Remodeling page: a package (or a custom project), the bathroom's
/// measurements, photos and goals, and who and where. Saved, emailed to the office (photos attached) and summarized
/// to the customer; staff follow up with an estimate.
/// </summary>
public sealed class BathroomRequest
{
    public Guid Id { get; set; }

    /// <summary>A key of <see cref="BathroomPackages.All"/>, or "custom".</summary>
    public required string Package { get; set; }

    /// <summary>The package's name at the time, e.g. "Walk-In Shower Conversion" ("Custom project" for custom).</summary>
    public required string PackageName { get; set; }

    /// <summary>The package's starting price in dollars at the time; null for a custom project.</summary>
    public int? StartingPrice { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    /// <summary>E.164.</summary>
    public required string Phone { get; set; }

    public required string StreetAddress { get; set; }

    public required string City { get; set; }

    /// <summary>A US state's full name, e.g. "Illinois".</summary>
    public required string State { get; set; }

    public required string ZipCode { get; set; }

    public string AddressLine => $"{StreetAddress}, {City}, {State} {ZipCode}";

    /// <summary>Bathroom length in feet, when known.</summary>
    public decimal? LengthFeet { get; set; }

    /// <summary>Bathroom width in feet, when known.</summary>
    public decimal? WidthFeet { get; set; }

    /// <summary>When they'd like to start: one of <see cref="BathroomPackages.Timelines"/>.</summary>
    public string? Timeline { get; set; }

    /// <summary>What they want done, in their words.</summary>
    public string? Goals { get; set; }

    public List<BathroomRequestPhoto> Photos { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A photo of the bathroom, as uploaded (the browser shrinks it to about 1600 px first).</summary>
public sealed class BathroomRequestPhoto
{
    public Guid Id { get; set; }

    public Guid BathroomRequestId { get; set; }

    public required string FileName { get; set; }

    /// <summary>image/jpeg, image/png or image/webp, checked against the file's first bytes.</summary>
    public required string ContentType { get; set; }

    public required byte[] Data { get; set; }
}

/// <summary>The packages on the Bathroom Remodeling page (keep in step with the frontend's bathroomPackages.ts).</summary>
public static class BathroomPackages
{
    public const string Custom = "custom";

    public static readonly IReadOnlyDictionary<string, (string Name, int StartingPrice)> All =
        new Dictionary<string, (string, int)>
        {
            ["tub-shower-surround"] = ("Tub & Shower Surround Special", 3000),
            ["flooring-makeover"] = ("Bathroom Flooring Makeover", 4500),
            ["bathtub-replacement"] = ("Bathtub Replacement & Surround", 6500),
            ["vanity-refresh"] = ("Bathroom Vanity & Refresh", 8000),
            ["walk-in-shower"] = ("Walk-In Shower Conversion", 12000),
            ["premium-renovation"] = ("Premium Bathroom Renovation", 16000),
            ["luxury-remodel"] = ("Complete Luxury Bathroom Remodel", 20000)
        };

    /// <summary>When they'd like to start, as the form offers it.</summary>
    public static readonly string[] Timelines =
        ["As soon as possible", "Within 1–3 months", "Within 3–6 months", "Just exploring"];
}
