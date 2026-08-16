using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace AzureBuddy.Api;

/// <summary>
/// The partition-key logic behind the "Auth" rate limiter policy, pulled out of Program.cs's
/// AddRateLimiter call so it can be unit tested directly against a fake HttpContext rather than only
/// through a real HTTP pipeline. Partitioning by RemoteIpAddress is the actual fix for the bug this
/// existed to close: the previous AddFixedWindowLimiter call created exactly ONE limiter shared by every
/// caller, so five requests from anyone exhausted the budget for every other IP until the window reset.
/// </summary>
public static class AuthRateLimiterPolicy
{
    public static RateLimitPartition<string> CreatePartition(HttpContext httpContext, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
