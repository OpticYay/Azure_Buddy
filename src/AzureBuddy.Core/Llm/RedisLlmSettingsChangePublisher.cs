using AzureBuddy.Core.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AzureBuddy.Core.Llm;

public sealed class RedisLlmSettingsChangePublisher : ILlmSettingsChangePublisher
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisOptions _options;

    public RedisLlmSettingsChangePublisher(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> options)
    {
        _multiplexer = multiplexer;
        _options = options.Value;
    }

    /// <summary>Fire-and-forget in spirit even though it's awaited: the message payload is empty
    /// because every subscriber reacts by re-reading the row from MySQL (the source of truth) rather
    /// than trusting anything carried on the message itself - so a subscriber that's briefly
    /// disconnected and misses this publish just stays on stale settings until the next save, exactly
    /// like it would if it had never subscribed at all. No delivery guarantee is needed or assumed.</summary>
    public Task PublishAsync(CancellationToken cancellationToken = default) =>
        _multiplexer.GetSubscriber().PublishAsync(
            RedisChannel.Literal($"{_options.InstanceName}{LlmSettingsChangeChannel.Suffix}"), RedisValue.EmptyString);
}
