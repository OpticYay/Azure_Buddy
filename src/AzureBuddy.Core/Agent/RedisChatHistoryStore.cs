using AzureBuddy.Core.Caching;
using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AzureBuddy.Core.Agent;

/// <summary>
/// Redis-backed IChatHistoryStore - the shared cache that lets every replica of the API see the same
/// conversation window, fixing the original ChatHistoryStore's per-process-singleton bug. MySQL (via
/// IChatHistorySource) stays the source of truth; a Redis miss, a corrupt/unreadable payload, or a
/// schema-version bump all fall through to rehydration exactly like InMemoryChatHistoryStore does, so
/// Redis being flushed or unreachable degrades conversation continuity rather than breaking the app.
///
/// Each session is one Redis hash (VersionField + DataField) rather than a plain string, because a plain
/// SET/GET pair can't express "only write if nobody else already changed this" - the hash's VersionField
/// is what Condition.HashEqual/HashNotExists below compares against for optimistic concurrency.
/// </summary>
public sealed class RedisChatHistoryStore : IChatHistoryStore
{
    private const string VersionField = "version";
    private const string DataField = "data";
    private const int MaxCasAttempts = 3;

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IChatHistorySource _source;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisChatHistoryStore> _logger;

    public RedisChatHistoryStore(
        IConnectionMultiplexer multiplexer,
        IChatHistorySource source,
        IOptions<RedisOptions> options,
        ILogger<RedisChatHistoryStore> logger)
    {
        _multiplexer = multiplexer;
        _source = source;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ChatSessionWindow> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var entries = await db.HashGetAsync(Key(sessionId), new RedisValue[] { VersionField, DataField });
        var version = entries[0];
        var data = entries[1];

        if (!data.IsNullOrEmpty)
        {
            var messages = ChatWindowSerialization.Deserialize(data!);
            if (messages is not null)
            {
                return new ChatSessionWindow(sessionId, messages, version.ToString());
            }
        }

        var rehydrated = await _source.LoadRecentAsync(sessionId, ChatSessionWindow.DefaultWindowSize, cancellationToken);

        // A hash that exists but whose data field is missing/corrupt/unversioned still has a real
        // VersionField a concurrent writer could be racing against - preserve it so SaveAsync CASes
        // against the actual current state instead of assuming (via string.Empty) that no row exists yet.
        return new ChatSessionWindow(sessionId, rehydrated, version.IsNullOrEmpty ? string.Empty : version.ToString());
    }

    public async Task SaveAsync(ChatSessionWindow window, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var key = Key(window.SessionId);
        var appended = window.Appended.ToList();
        var current = window;

        for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
        {
            var newVersion = Guid.NewGuid().ToString();
            var json = ChatWindowSerialization.Serialize(current.Messages);

            var tx = db.CreateTransaction();
            tx.AddCondition(string.IsNullOrEmpty(current.Version)
                ? Condition.HashNotExists(key, VersionField)
                : Condition.HashEqual(key, VersionField, current.Version));
            _ = tx.HashSetAsync(key, new[]
            {
                new HashEntry(VersionField, newVersion),
                new HashEntry(DataField, json),
            });
            _ = tx.KeyExpireAsync(key, TimeSpan.FromHours(_options.ChatWindowTtlHours));

            var committed = await tx.ExecuteAsync();
            if (committed)
            {
                return;
            }

            // Lost the race: someone else (another replica handling a concurrent request for the same
            // session) wrote first. Reload the state they just wrote, replay only what THIS turn added
            // on top of it, and retry - rather than either overwriting their turn or discarding ours.
            current = await LoadAsync(window.SessionId, cancellationToken);
            foreach (var message in appended)
            {
                current.Add(message);
            }
        }

        // Redis is a cache, not the source of truth - if three concurrent replicas are all racing to
        // save the same session, give up on this write rather than retry forever. The next LoadAsync
        // for this session (this replica or another) simply rehydrates from MySQL again on its next miss
        // once the winning write's TTL... actually the winning write IS in Redis, so the next load just
        // sees whichever turn won the race and this turn's reply is only missing from the shared cache,
        // not lost - it was already persisted to MySQL by the caller before Save was ever called.
        _logger.LogWarning(
            "Gave up caching chat window for session {SessionId} after {Attempts} concurrent-write conflicts",
            window.SessionId, MaxCasAttempts);
    }

    public Task EvictAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _multiplexer.GetDatabase().KeyDeleteAsync(Key(sessionId));

    private string Key(string sessionId) => $"{_options.InstanceName}chat:{sessionId}";
}
