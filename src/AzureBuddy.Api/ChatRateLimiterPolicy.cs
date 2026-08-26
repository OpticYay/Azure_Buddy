using System.Threading.RateLimiting;
using AzureBuddy.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace AzureBuddy.Api;

/// <summary>
/// The partition-key logic behind the "Chat" rate limiter policy (see RateLimiterPolicies.Chat).
/// Partitioned per authenticated user rather than per IP (unlike AuthRateLimiterPolicy): ChatController
/// requires [Authorize], and app.UseRateLimiter() now runs after app.UseAuthorization() specifically so
/// the verified user id claim is available here - see Program.cs's middleware ordering comment. Per-user
/// is both more precise (one user behind a shared/NAT'd IP can't be starved by another) and harder to
/// route around than per-IP (an attacker can't dodge it by rotating source addresses without also
/// rotating stolen credentials).
/// </summary>
public static class ChatRateLimiterPolicy
{
    public static RateLimitPartition<string> CreatePartition(HttpContext httpContext, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.IsAuthenticated == true
                ? httpContext.User.GetRequiredUserId()
                : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
