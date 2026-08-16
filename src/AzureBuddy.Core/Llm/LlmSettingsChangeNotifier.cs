using AzureBuddy.Core.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AzureBuddy.Core.Llm;

/// <summary>
/// The other half of the LLM settings hot-reload story: LlmSettingsService.SaveAsync already updates the
/// SAVING replica's ILlmSettingsProvider directly, but every OTHER replica behind the load balancer would
/// otherwise keep running on stale settings until its own next restart. This subscribes to
/// RedisLlmSettingsChangePublisher's channel for the lifetime of the app and, on every message, re-reads
/// the LlmSettings row from MySQL and refreshes this replica's own provider - the exact same
/// LoadFromDatabaseIfPresentAsync call Program.cs already makes once at startup.
///
/// <paramref name="multiplexer"/> is optional (null when Redis isn't configured) rather than gated by a
/// separate DI registration switch like RedisChatHistoryStore's: this hosted service is registered
/// unconditionally, and simply does nothing in StartAsync when there's no multiplexer to subscribe with
/// - a single-instance deployment has no other replicas to hear from in the first place.
/// </summary>
public sealed class LlmSettingsChangeNotifier : IHostedService
{
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly RedisOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LlmSettingsChangeNotifier> _logger;
    private RedisChannel _channel;

    public LlmSettingsChangeNotifier(
        IServiceScopeFactory scopeFactory,
        IOptions<RedisOptions> options,
        ILogger<LlmSettingsChangeNotifier> logger,
        IConnectionMultiplexer? multiplexer = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _multiplexer = multiplexer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_multiplexer is null)
        {
            return;
        }

        _channel = RedisChannel.Literal($"{_options.InstanceName}{LlmSettingsChangeChannel.Suffix}");
        await _multiplexer.GetSubscriber().SubscribeAsync(_channel, (_, _) => _ = OnChangedAsync());
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.GetSubscriber().UnsubscribeAsync(_channel);
        }
    }

    // LlmSettingsService is Scoped (it depends on the Scoped AppDbContext), so a pub/sub handler running
    // on Redis's own callback thread - outside any request scope - has to create one explicitly, the
    // same reason RedisChatHistoryStore's own dependencies are split into Scoped-vs-Singleton pieces.
    private async Task OnChangedAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<LlmSettingsService>();
            await settingsService.LoadFromDatabaseIfPresentAsync();
        }
        catch (Exception ex)
        {
            // Never let a failed refresh crash the subscriber loop - this replica just keeps running on
            // whatever settings it already has until the next successful notification.
            _logger.LogWarning(ex, "Failed to refresh LLM settings after a pub/sub change notification.");
        }
    }
}
