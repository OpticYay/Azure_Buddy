using Testcontainers.Redis;

namespace AzureBuddy.Tests.Integration;

/// <summary>
/// One real Redis container for the entire test run, mirroring SharedMySqlContainer's shape and
/// reasoning: too slow to start per test, so it's started lazily on first use and shared by every
/// CustomWebApplicationFactory instance that opts into Redis for the rest of the process's lifetime.
/// Test isolation comes from each factory using its own RedisOptions.InstanceName key prefix (see
/// CustomWebApplicationFactory's useRedis constructor path), the same shape SharedMySqlContainer gets
/// via a uniquely-named database per factory rather than a container per factory.
/// </summary>
internal static class SharedRedisContainer
{
    private static readonly Lazy<Task<RedisContainer>> LazyContainer = new(StartAsync);

    public static Task<RedisContainer> GetAsync() => LazyContainer.Value;

    private static async Task<RedisContainer> StartAsync()
    {
        var container = new RedisBuilder("redis:7").Build();
        await container.StartAsync();
        return container;
    }
}
