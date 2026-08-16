using System.Net;
using System.Threading.RateLimiting;
using AzureBuddy.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AzureBuddy.Tests.RateLimiting;

/// <summary>
/// Regression coverage for the actual bug: the previous AddFixedWindowLimiter call created exactly ONE
/// limiter shared by every caller, so five requests from anyone exhausted the same shared budget for
/// every other client's IP until the window reset - despite the code's own comment claiming "per client
/// IP." AuthRateLimiterPolicy.CreatePartition is what Program.cs's AddPolicy call now uses instead;
/// exercising it through a real PartitionedRateLimiter (not just asserting on the partition key string)
/// is what actually proves two different IPs get two independent budgets.
/// </summary>
public class AuthRateLimiterPolicyTests
{
    private static HttpContext ContextWithIp(string ip)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    [Fact]
    public void TwoDifferentIps_EachGetTheirOwnIndependentBudget()
    {
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => AuthRateLimiterPolicy.CreatePartition(httpContext, permitLimit: 2));

        var ipA = ContextWithIp("1.1.1.1");
        var ipB = ContextWithIp("2.2.2.2");

        Assert.True(limiter.AttemptAcquire(ipA).IsAcquired);
        Assert.True(limiter.AttemptAcquire(ipA).IsAcquired);
        // IP A's budget of 2 is now exhausted.
        Assert.False(limiter.AttemptAcquire(ipA).IsAcquired);

        // IP B was never rate-limited by A's requests - this is the exact behavior the old
        // single-shared-limiter bug got wrong: A exhausting its own budget must not affect B at all.
        Assert.True(limiter.AttemptAcquire(ipB).IsAcquired);
        Assert.True(limiter.AttemptAcquire(ipB).IsAcquired);
        Assert.False(limiter.AttemptAcquire(ipB).IsAcquired);
    }

    [Fact]
    public void SameIp_SharesOneBudgetAcrossRequests()
    {
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => AuthRateLimiterPolicy.CreatePartition(httpContext, permitLimit: 1));

        Assert.True(limiter.AttemptAcquire(ContextWithIp("3.3.3.3")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(ContextWithIp("3.3.3.3")).IsAcquired);
    }

    [Fact]
    public void NullRemoteIpAddress_FallsBackToASingleSharedUnknownPartition()
    {
        var partition = AuthRateLimiterPolicy.CreatePartition(new DefaultHttpContext(), permitLimit: 5);

        Assert.Equal("unknown", partition.PartitionKey);
    }
}
