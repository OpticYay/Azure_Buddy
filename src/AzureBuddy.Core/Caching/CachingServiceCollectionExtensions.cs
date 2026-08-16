using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace AzureBuddy.Core.Caching;

public static class CachingServiceCollectionExtensions
{
    /// <summary>Binds RedisOptions and, when Redis is configured, registers the already-constructed
    /// multiplexer (see RedisConnectionFactory.TryCreate, which Program.cs calls before the container is
    /// built) as a singleton. When `multiplexer` is null, no IConnectionMultiplexer is registered at
    /// all - downstream consumers (chat history store, pub/sub notifier) must resolve it as
    /// IConnectionMultiplexer? and branch on absence rather than assume it's always there.</summary>
    public static IServiceCollection AddAzureBuddyRedis(
        this IServiceCollection services, IConfiguration configuration, IConnectionMultiplexer? multiplexer)
    {
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));

        if (multiplexer is not null)
        {
            services.AddSingleton(multiplexer);
        }

        return services;
    }
}
