using System.Net;
using AzureBuddy.Core.Auth;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace AzureBuddy.Api.Extensions;

public static class RateLimiterServiceCollectionExtensions
{
    /// <summary>
    /// Fixed-window limiter on the auth endpoints only, partitioned per client IP via AddPolicy/
    /// RateLimitPartition.GetFixedWindowLimiter: 5 requests per minute PER IP. This is a basic
    /// brute-force/registration-spam speed bump, not a substitute for account lockout (Identity's
    /// lockout policy already handles "too many wrong passwords for one account").
    ///
    /// This used to be AddFixedWindowLimiter, which creates exactly ONE limiter shared by every caller
    /// regardless of who they are - despite the "per client IP" framing, there was no partitioning at
    /// all, so five requests from anyone, anywhere, exhausted the shared budget for every other user
    /// until the window reset. AddPolicy with a partition key function is what actually makes each IP
    /// get its own independent budget.
    ///
    /// Also configures forwarded-headers handling: the limiter partitions by
    /// HttpContext.Connection.RemoteIpAddress - behind any reverse proxy/load balancer, that's always
    /// the PROXY's own IP unless ForwardedHeadersOptions rewrites it from X-Forwarded-For first.
    /// KnownNetworks/KnownProxies are cleared rather than left at their ASP.NET Core defaults (which
    /// only trust literal loopback) and rather than defaulting to "trust anything": an unconfigured
    /// deployment gets today's behavior (every client is rate-limited together under the proxy's IP -
    /// wrong, but not attacker-controlled), and Network:KnownProxies must be set to a deployment's
    /// actual reverse-proxy IP(s) before X-Forwarded-For is honored at all. Blindly trusting
    /// X-Forwarded-For with no configured proxy would let any client set their own "IP" and either
    /// dodge the rate limit or frame another client under it.
    /// </summary>
    /// <param name="isTestingEnvironment">
    /// True gives an effectively unlimited permit count: WebApplicationFactory-based integration tests
    /// all originate from the TestServer's single synthetic client IP, so a real-world per-IP limit of
    /// 5/minute trips almost immediately once more than a handful of tests run against the same factory
    /// instance - this isn't a workaround for a bug, it's the same limiter correctly doing its job
    /// against traffic that (unlike real clients) has no IP diversity. Production behavior is unchanged.
    /// </param>
    public static IServiceCollection AddAzureBuddyRateLimiting(this IServiceCollection services, IConfiguration configuration, bool isTestingEnvironment)
    {
        var knownProxies = (configuration.GetSection("Network:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
            .Select(proxy => IPAddress.TryParse(proxy, out var proxyIp) ? proxyIp : null)
            .Where(proxyIp => proxyIp is not null)
            .ToList();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxyIp in knownProxies)
            {
                options.KnownProxies.Add(proxyIp!);
            }
        });

        var authRateLimit = isTestingEnvironment ? int.MaxValue : 5;

        // ChatController's /api/chat makes at least one billed LLM request (and potentially several
        // Azure DevOps API calls) on every call - unlike Auth above, there was previously no rate limit
        // on it at all, which is both a cost-control gap and an abuse-protection gap. Same "Testing"
        // carve-out as Auth: the TestServer's single synthetic client/user would otherwise trip a
        // real-world limit almost immediately once more than a handful of tests exercise the same
        // factory instance.
        var chatRateLimit = isTestingEnvironment ? int.MaxValue : 20;
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimiterPolicies.Auth, httpContext => AuthRateLimiterPolicy.CreatePartition(httpContext, authRateLimit));
            options.AddPolicy(RateLimiterPolicies.Chat, httpContext => ChatRateLimiterPolicy.CreatePartition(httpContext, chatRateLimit));
        });

        return services;
    }
}
