namespace AzureBuddy.Core.Agent;

/// <summary>
/// Replaces the old ChatHistoryStore.GetOrCreate, which handed out a mutable ChatHistory that callers
/// mutated in place with no write-back - a contract that can't cross a network. Load/Save/Evict instead:
/// a caller loads a per-turn ChatSessionWindow, mutates it locally, and explicitly saves it back when
/// (and only when) the turn produced something worth keeping.
/// </summary>
public interface IChatHistoryStore
{
    /// <summary>Never returns null: a cache miss falls through to IChatHistorySource and returns a
    /// freshly-rehydrated (possibly empty) window rather than throwing.</summary>
    Task<ChatSessionWindow> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(ChatSessionWindow window, CancellationToken cancellationToken = default);

    Task EvictAsync(string sessionId, CancellationToken cancellationToken = default);
}
