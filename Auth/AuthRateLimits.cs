using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

public static class AuthRateLimits
{
    /// <summary>Public login endpoints: limits code texts (they cost money) and guessing, per client IP.</summary>
    public const string Policy = "auth";

    public static void AddAuthRateLimits(this RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));
    }
}
