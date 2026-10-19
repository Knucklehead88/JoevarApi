using Microsoft.EntityFrameworkCore;

/// <summary>One page of results plus the total count, for list endpoints.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var currentPage = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((currentPage - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, total, currentPage, size);
    }
}
