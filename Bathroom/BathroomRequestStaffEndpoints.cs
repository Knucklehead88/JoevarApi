using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Staff read the Bathroom Remodel page's requests (newest first) and their photos. Photos are served one at a time
/// with the staff token, so the dashboard fetches them and shows them as blob URLs (an img tag can't send the token).
/// </summary>
public static class BathroomRequestStaffEndpoints
{
    public static IEndpointRouteBuilder MapBathroomRequestStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/bathroom-requests")
            .WithTags("Bathroom")
            .RequireAuthorization(AuthPolicies.Staff);

        group.MapGet("/", ListRequestsAsync).WithName("ListBathroomRequests").WithSummary("List bathroom project requests, newest first");
        group.MapGet("/{id:guid}/photos/{photoId:guid}", GetPhotoAsync).WithName("GetBathroomRequestPhoto").WithSummary("One photo of a bathroom request");

        return endpoints;
    }

    private static async Task<Ok<PagedResult<BathroomRequestResponse>>> ListRequestsAsync(
        AppDbContext db,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var result = await db.BathroomRequests.AsNoTracking()
            .OrderByDescending(request => request.CreatedAt)
            .Select(request => new BathroomRequestResponse(
                request.Id,
                request.Package,
                request.PackageName,
                request.StartingPrice,
                request.Name,
                request.Email,
                request.Phone,
                request.StreetAddress,
                request.City,
                request.State,
                request.ZipCode,
                request.LengthFeet,
                request.WidthFeet,
                request.Timeline,
                request.Goals,
                // Names only: the bytes come from the photo endpoint.
                request.Photos.Select(photo => new BathroomPhotoInfo(photo.Id, photo.FileName)).ToList(),
                request.CreatedAt))
            .ToPagedResultAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> GetPhotoAsync(
        Guid id,
        Guid photoId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var photo = await db.BathroomRequests.AsNoTracking()
            .Where(request => request.Id == id)
            .SelectMany(request => request.Photos)
            .Where(photo => photo.Id == photoId)
            .Select(photo => new { photo.Data, photo.ContentType, photo.FileName })
            .FirstOrDefaultAsync(cancellationToken);
        return photo is null
            ? TypedResults.NotFound()
            : TypedResults.File(photo.Data, photo.ContentType, photo.FileName);
    }
}

public sealed record BathroomPhotoInfo(Guid Id, string FileName);

/// <summary>A bathroom project request; <see cref="StartingPrice"/> is null for a custom project.</summary>
public sealed record BathroomRequestResponse(
    Guid Id,
    string Package,
    string PackageName,
    int? StartingPrice,
    string Name,
    string Email,
    string Phone,
    string StreetAddress,
    string City,
    string State,
    string ZipCode,
    decimal? LengthFeet,
    decimal? WidthFeet,
    string? Timeline,
    string? Goals,
    List<BathroomPhotoInfo> Photos,
    DateTimeOffset CreatedAt);
