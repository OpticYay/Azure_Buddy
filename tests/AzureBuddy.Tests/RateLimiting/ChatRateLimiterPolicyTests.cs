using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AzureBuddy.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AzureBuddy.Tests.RateLimiting;

/// <summary>
/// Coverage for ChatRateLimiterPolicy.CreatePartition - unlike AuthRateLimiterPolicy (per client IP),
/// this partitions by the authenticated user's id so two users sharing an IP (e.g. behind the same NAT)
/// each get their own budget, and falls back to IP only for the (should-be-unreachable-behind
/// [Authorize]) case of no authenticated user.
/// </summary>
public class ChatRateLimiterPolicyTests
{
    private static HttpContext AuthenticatedContext(string userId, string ip = "1.1.1.1")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "TestAuth");
        context.User = new ClaimsPrincipal(identity);
        return context;
    }

    [Fact]
    public void TwoDifferentUsers_OnTheSameIp_EachGetTheirOwnIndependentBudget()
    {
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => ChatRateLimiterPolicy.CreatePartition(httpContext, permitLimit: 1));

        var userA = AuthenticatedContext("user-a", ip: "9.9.9.9");
        var userB = AuthenticatedContext("user-b", ip: "9.9.9.9");

        Assert.True(limiter.AttemptAcquire(userA).IsAcquired);
        Assert.False(limiter.AttemptAcquire(userA).IsAcquired);

        // Same IP as user A, but a different authenticated user - must not be affected by A's budget.
        Assert.True(limiter.AttemptAcquire(userB).IsAcquired);
    }

    [Fact]
    public void SameUser_SharesOneBudgetAcrossRequests()
    {
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => ChatRateLimiterPolicy.CreatePartition(httpContext, permitLimit: 1));

        Assert.True(limiter.AttemptAcquire(AuthenticatedContext("user-c")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(AuthenticatedContext("user-c")).IsAcquired);
    }

    [Fact]
    public void Unauthenticated_FallsBackToRemoteIpAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("4.4.4.4");

        var partition = ChatRateLimiterPolicy.CreatePartition(context, permitLimit: 5);

        Assert.Equal("4.4.4.4", partition.PartitionKey);
    }
}
