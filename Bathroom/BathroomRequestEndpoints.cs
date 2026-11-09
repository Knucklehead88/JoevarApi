using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;

/// <summary>
/// The Bathroom Remodeling page's questionnaire ("Start this project" on a package, or a custom project): anyone can
/// send their bathroom's measurements, photos and goals. Saved, emailed to the office with the photos attached (the
/// customer as Reply-To), and a summary emailed to the customer. Shares the Contact form's rate limit.
/// </summary>
public static class BathroomRequestEndpoints
{
    public const int MaxPhotos = 8;

    /// <summary>Per photo, after the browser has shrunk it; a phone photo at 1600 px is usually well under 1 MB.</summary>
    public const int MaxPhotoBytes = 3 * 1024 * 1024;

    public static IEndpointRouteBuilder MapBathroomRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/bathroom-requests", CreateRequestAsync)
            .WithTags("Bathroom")
            .RequireRateLimiting(ContactEndpoints.RateLimitPolicy)
            .WithName("CreateBathroomRequest")
            .WithSummary("Send a bathroom project request with measurements, photos and goals");

        return endpoints;
    }

    private static async Task<Results<NoContent, ValidationProblem>> CreateRequestAsync(
        BathroomRequestBody request,
        AppDbContext db,
        UsCityDirectory cities,
        OfficeEmailSender emailSender,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var packageKey = request.Package.Trim();
        (string Name, int StartingPrice)? package = null;
        if (packageKey != BathroomPackages.Custom)
        {
            if (BathroomPackages.All.TryGetValue(packageKey, out var known))
            {
                package = known;
            }
            else
            {
                errors["package"] = ["Choose one of the packages or a custom project."];
            }
        }

        if (packageKey == BathroomPackages.Custom && string.IsNullOrWhiteSpace(request.Goals))
        {
            errors["goals"] = ["Tell us what you have in mind for your bathroom."];
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            errors["phone"] = ["Phone must be a valid phone number, e.g. +17735550100."];
        }

        if (request.Timeline is not null && !BathroomPackages.Timelines.Contains(request.Timeline))
        {
            errors["timeline"] = [$"Choose one of: {string.Join(", ", BathroomPackages.Timelines)}."];
        }

        var photos = new List<BathroomRequestPhoto>();
        foreach (var photo in request.Photos)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(photo.Data);
            }
            catch (FormatException)
            {
                errors["photos"] = [$"“{photo.FileName}” couldn't be read. Try another photo."];
                break;
            }

            var contentType = ImageType(data);
            if (contentType is null)
            {
                errors["photos"] = [$"“{photo.FileName}” isn't a JPEG, PNG or WebP photo."];
                break;
            }

            if (data.Length > MaxPhotoBytes)
            {
                errors["photos"] = [$"“{photo.FileName}” is too large (up to {MaxPhotoBytes / 1024 / 1024} MB each)."];
                break;
            }

            var fileName = Path.GetFileName(photo.FileName.Trim());
            photos.Add(new BathroomRequestPhoto
            {
                FileName = string.IsNullOrEmpty(fileName) ? $"photo-{photos.Count + 1}" : fileName,
                ContentType = contentType,
                Data = data
            });
        }

        var state = UsCityDirectory.FindState(request.State);
        var city = request.City.Trim();
        if (state is null)
        {
            errors["state"] = ["Choose a US state."];
        }
        else
        {
            // Unknown when the city service is down: accepted as typed rather than refusing the request.
            var found = await cities.FindCityAsync(state, city, cancellationToken);
            if (found == "")
            {
                errors["city"] = [$"Choose a city in {state}."];
            }
            else if (found is not null)
            {
                city = found;
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var bathroom = new BathroomRequest
        {
            Package = packageKey,
            PackageName = package?.Name ?? "Custom project",
            StartingPrice = package?.StartingPrice,
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            Phone = phone!,
            StreetAddress = request.StreetAddress.Trim(),
            City = city,
            State = state!,
            ZipCode = request.ZipCode.Trim(),
            LengthFeet = request.LengthFeet,
            WidthFeet = request.WidthFeet,
            Timeline = request.Timeline,
            Goals = string.IsNullOrWhiteSpace(request.Goals) ? null : request.Goals.Trim(),
            Photos = photos
        };
        db.BathroomRequests.Add(bathroom);
        await db.SaveChangesAsync(cancellationToken);
        var logger = loggerFactory.CreateLogger("BathroomRequest");
        logger.LogInformation("A bathroom project request was received with {Photos} photo(s).", photos.Count);

        // Saved first, so a mail problem never loses a request or shows the customer an error.
        try
        {
            await emailSender.SendAsync(
                $"Bathroom project request: {bathroom.Name}, {bathroom.PackageName}",
                $"""
                New bathroom project request from the Bathroom Remodeling page.

                Package: {PackageLine(bathroom)}

                Name:  {bathroom.Name}
                Email: {bathroom.Email}
                Phone: {bathroom.Phone}
                Address: {bathroom.AddressLine}

                Bathroom size: {SizeLine(bathroom)}
                Timeline: {bathroom.Timeline ?? "-"}

                Goals:
                {bathroom.Goals ?? "-"}

                Photos: {(photos.Count == 0 ? "none" : $"{photos.Count}, attached")}

                Reply to this email to answer {bathroom.Name} with the next steps.
                """,
                bathroom.Name,
                bathroom.Email,
                cancellationToken,
                photos.Select(photo => new EmailAttachment(photo.FileName, photo.ContentType, photo.Data)).ToList());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Bathroom request {Id} was saved but could not be emailed.", bathroom.Id);
        }

        try
        {
            await emailSender.SendToCustomerAsync(
                bathroom.Name,
                bathroom.Email,
                "We received your bathroom project request",
                $"""
                Hi {bathroom.Name},

                Thank you for telling us about your bathroom project. Our team will review it and contact you with the
                next steps toward your renovation and your estimate.

                Package: {PackageLine(bathroom)}
                Address: {bathroom.AddressLine}
                Bathroom size: {SizeLine(bathroom)}
                Timeline: {bathroom.Timeline ?? "-"}
                Photos: {photos.Count}

                Your goals:
                {bathroom.Goals ?? "-"}

                Prices are starting prices; your estimate depends on your location, the bathroom's size, materials,
                labor, plumbing and how complex the project is. To add anything, just reply to this email.

                Joevar
                """,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Bathroom request {Id} was saved but the customer's email could not be sent.", bathroom.Id);
        }

        return TypedResults.NoContent();
    }

    private static string PackageLine(BathroomRequest request) =>
        request.StartingPrice is { } price
            ? $"{request.PackageName} (starting at {price.ToString("C0", CultureInfo.GetCultureInfo("en-US"))})"
            : request.PackageName;

    private static string SizeLine(BathroomRequest request) =>
        request.LengthFeet is null && request.WidthFeet is null
            ? "-"
            : $"{request.LengthFeet?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} ft × {request.WidthFeet?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} ft";

    /// <summary>The image type from the file's first bytes (not the name or what the browser claimed), or null.</summary>
    private static string? ImageType(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null
    };
}

public sealed record BathroomRequestBody
{
    /// <summary>A package key (e.g. "walk-in-shower") or "custom".</summary>
    [Required]
    [MaxLength(40)]
    public required string Package { get; init; }

    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }

    [Required]
    [MaxLength(200)]
    public required string StreetAddress { get; init; }

    [Required]
    [MaxLength(100)]
    public required string City { get; init; }

    [Required]
    [MaxLength(40)]
    public required string State { get; init; }

    [Required]
    [RegularExpression(@"^\s*\d{5}(-\d{4})?\s*$", ErrorMessage = "Enter a 5-digit ZIP code, e.g. 60130.")]
    public required string ZipCode { get; init; }

    [Range(1, 100, ErrorMessage = "Enter the length in feet, e.g. 8.")]
    public decimal? LengthFeet { get; init; }

    [Range(1, 100, ErrorMessage = "Enter the width in feet, e.g. 5.")]
    public decimal? WidthFeet { get; init; }

    [MaxLength(40)]
    public string? Timeline { get; init; }

    [MaxLength(4000)]
    public string? Goals { get; init; }

    [MaxLength(BathroomRequestEndpoints.MaxPhotos, ErrorMessage = "Add up to 8 photos.")]
    public List<BathroomPhotoUpload> Photos { get; init; } = [];
}

/// <summary>A photo as base64 (the browser shrinks it to about 1600 px and JPEG first).</summary>
public sealed record BathroomPhotoUpload
{
    [Required]
    [MaxLength(200)]
    public required string FileName { get; init; }

    /// <summary>The file's bytes, base64.</summary>
    [Required]
    public required string Data { get; init; }
}
