using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AzureBuddy.Core.Caching;

/// <summary>Static factory, not a DI registration. Program.cs needs the live multiplexer at
/// configuration time - before the service container is built - to hand to the Data Protection
/// builder, so this can't wait for the usual "resolve it from DI when something asks" pattern.
/// Everything else that wants a multiplexer resolves the singleton CachingServiceCollectionExtensions
/// registers from the instance this returns.</summary>
public static class RedisConnectionFactory
{
    /// <summary>Null when Redis:Configuration is blank. Callers MUST treat that as "run with today's
    /// in-memory/per-instance behaviour," not as a startup failure - `dotnet run` and every
    /// WebApplicationFactory-based integration test build the full host with no Redis available, and
    /// none of them should be forced to stand one up just to boot.</summary>
    public static IConnectionMultiplexer? TryCreate(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        var connectionString = configuration[$"{RedisOptions.SectionName}:Configuration"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var options = ConfigurationOptions.Parse(connectionString);
        // A Redis restart mid-rolling-deploy must not take the API down with it. StackExchange.Redis
        // defaults AbortOnConnectFail to true (throw out of Connect/ConnectAsync on the first failed
        // attempt) - false lets the multiplexer keep retrying in the background instead, matching every
        // subsystem's designed-in "Redis is a cache/enhancement, MySQL is the source of truth" posture.
        options.AbortOnConnectFail = false;

        var logger = loggerFactory.CreateLogger("AzureBuddy.Redis");
        var multiplexer = ConnectionMultiplexer.Connect(options);
        multiplexer.ConnectionFailed += (_, args) =>
            logger.LogWarning(
                args.Exception, "Redis connection failed ({FailureType}) on {EndPoint}", args.FailureType, args.EndPoint);
        multiplexer.ConnectionRestored += (_, args) =>
            logger.LogInformation("Redis connection restored on {EndPoint}", args.EndPoint);

        return multiplexer;
    }
}
