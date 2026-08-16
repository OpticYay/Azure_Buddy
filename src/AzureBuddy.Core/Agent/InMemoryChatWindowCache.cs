using System.Collections.Concurrent;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Singleton backing store for InMemoryChatHistoryStore - the shared, process-lifetime state
/// that makes chat memory survive across requests when Redis isn't configured. Registered separately
/// from InMemoryChatHistoryStore itself (which is Scoped, matching every other collaborator) so the
/// Scoped store doesn't need DI gymnastics to reach a singleton it depends on.
///
/// Stores an immutable snapshot per session, replaced wholesale on every save - never mutated in place.
/// That's what actually removes the old ChatHistory race (Clear() + two AddRanges on a process-wide
/// shared object): a caller can only ever read a complete, consistent snapshot, and two racing saves
/// for the same session just mean the second write wins, not a torn read.</summary>
public sealed class InMemoryChatWindowCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ChatMessage>> _sessions = new();

    public bool TryGet(string sessionId, out IReadOnlyList<ChatMessage>? messages) =>
        _sessions.TryGetValue(sessionId, out messages);

    public void Set(string sessionId, IReadOnlyList<ChatMessage> messages) =>
        _sessions[sessionId] = messages;

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
