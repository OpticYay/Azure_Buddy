using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AzureBuddy.Core.Agent;

/// <summary>Singleton backing store for InMemoryChatHistoryStore - the shared, process-lifetime state
/// that makes chat memory survive across requests when Redis isn't configured. Registered separately
/// from InMemoryChatHistoryStore itself (which is Scoped, matching every other collaborator) so the
/// Scoped store doesn't need DI gymnastics to reach a singleton it depends on.
///
/// Stores an immutable snapshot per session, replaced wholesale on every save - never mutated in place.
/// That's what actually removes the old ChatHistory race (Clear() + two AddRanges on a process-wide
/// shared object): a caller can only ever read a complete, consistent snapshot, and two racing saves
/// for the same session just mean the second write wins, not a torn read.
///
/// Backed by IMemoryCache (SizeLimit + sliding expiration) rather than a raw ConcurrentDictionary: a
/// plain dictionary keeps every session's working memory resident for the lifetime of the process, with
/// no eviction, so a long-running single instance accumulates memory proportional to the total number
/// of distinct sessions ever touched, not the number of currently-active ones. Evicting an idle entry
/// here is safe - InMemoryChatHistoryStore.LoadAsync already falls back to re-reading recent history
/// from MySQL (via IChatHistorySource) on a cache miss, so eviction only costs one extra DB read on the
/// session's next message, never data loss.</summary>
public sealed class InMemoryChatWindowCache
{
    // One "unit" per session regardless of how many messages it holds - simplest workable size metric,
    // and avoids counting message character/token lengths just to bound entry count. 2000 sessions is a
    // deliberately generous cap for a single-instance deployment; tune via cache options if a real
    // deployment's session churn needs a different number.
    private const int MaxCachedSessions = 2000;
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromHours(2);

    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxCachedSessions });

    public bool TryGet(string sessionId, out IReadOnlyList<ChatMessage>? messages) =>
        _cache.TryGetValue(sessionId, out messages);

    public void Set(string sessionId, IReadOnlyList<ChatMessage> messages) =>
        _cache.Set(sessionId, messages, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = SlidingExpiration,
        });

    public void Remove(string sessionId) => _cache.Remove(sessionId);
}
