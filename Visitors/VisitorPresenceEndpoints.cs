using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// Live visitors for the staff dashboard. While a page of the website is open and visible, the browser pings every
/// 20 seconds with an anonymous random id and the page's path; whoever pinged within <see cref="LiveWindow"/> counts
/// as on the site now. Kept in memory only (nothing personal, no IP), so it starts empty after a restart and counts
/// per API instance.
/// </summary>
public static partial class VisitorPresenceEndpoints
{
    /// <summary>Limits pings per client IP (several tabs, or several people behind one router, share it).</summary>
    public const string RateLimitPolicy = "visitor-ping";

    /// <summary>How recently a visitor must have pinged to count as live (three missed pings).</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(60);

    // Guards memory against a flood of made-up ids; far above any real number of simultaneous visitors.
    private const int MaxTracked = 10_000;

    private static readonly ConcurrentDictionary<string, Presence> Visitors = new();

    private sealed record Presence(string Path, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

    public static void AddVisitorPingRateLimit(this RateLimiterOptions options) =>
        options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));

    public static IEndpointRouteBuilder MapVisitorPresenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/visitors/ping", Ping)
            .WithTags("Visitors")
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("PingVisitor")
            .WithSummary("Tell the API this visitor is on the website now (sent by the site every 20 seconds)");

        endpoints.MapGet("/api/visitors/live", GetLive)
            .WithTags("Visitors")
            .RequireAuthorization(AuthPolicies.Staff)
            .WithName("GetLiveVisitors")
            .WithSummary("Visitors on the website now, and on which pages");

        return endpoints;
    }

    private static Results<NoContent, ValidationProblem> Ping(VisitorPingRequest request)
    {
        if (!VisitorIdPattern().IsMatch(request.VisitorId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["visitorId"] = ["Must be 8 to 64 letters, digits or dashes."]
            });
        }

        var now = DateTimeOffset.UtcNow;
        RemoveExpired(now);
        var path = request.Path.Trim();
        if (Visitors.Count < MaxTracked || Visitors.ContainsKey(request.VisitorId))
        {
            Visitors.AddOrUpdate(
                request.VisitorId,
                _ => new Presence(path, now, now),
                (_, previous) => previous with { Path = path, LastSeen = now });
        }

        return TypedResults.NoContent();
    }

    private static Ok<LiveVisitorsResponse> GetLive()
    {
        var now = DateTimeOffset.UtcNow;
        RemoveExpired(now);
        var live = Visitors.Values.Where(presence => now - presence.LastSeen <= LiveWindow).ToList();
        var pages = live
            .GroupBy(presence => presence.Path)
            .Select(group => new LivePage(group.Key, group.Count()))
            .OrderByDescending(page => page.Visitors)
            .ThenBy(page => page.Path)
            .ToList();
        return TypedResults.Ok(new LiveVisitorsResponse(live.Count, pages));
    }

    private static void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (id, presence) in Visitors)
        {
            if (now - presence.LastSeen > LiveWindow)
            {
                Visitors.TryRemove(id, out _);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex VisitorIdPattern();
}

public sealed record VisitorPingRequest
{
    /// <summary>A random id the browser keeps for this visitor (not tied to any account).</summary>
    [Required]
    [MaxLength(64)]
    public required string VisitorId { get; init; }

    /// <summary>The page they're on, e.g. "/kitchen-design".</summary>
    [Required]
    [MaxLength(200)]
    public required string Path { get; init; }
}

public sealed record LiveVisitorsResponse(int Total, List<LivePage> Pages);

public sealed record LivePage(string Path, int Visitors);
