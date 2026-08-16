using System.Net;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>Both endpoints must work with no Authorization header at all - an orchestrator's health
/// probe never carries a bearer token, and Program.cs's global FallbackPolicy requires authentication
/// on every endpoint that doesn't explicitly opt out via AllowAnonymous. IntegrationTestBase.Client is
/// unauthenticated by default, which is exactly the client shape these tests need.</summary>
public class HealthEndpointsTests : IntegrationTestBase
{
    public HealthEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Live_Unauthenticated_ReturnsHealthy()
    {
        var response = await Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Unauthenticated_DatabaseReachable_ReturnsHealthy()
    {
        var response = await Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_WithRedisConfigured_AlsoReflectsRedisReachability()
    {
        await using var factory = CustomWebApplicationFactory.CreateWithRedis();
        await factory.InitializeAsync();
        using var client = factory.CreateHttpsClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
