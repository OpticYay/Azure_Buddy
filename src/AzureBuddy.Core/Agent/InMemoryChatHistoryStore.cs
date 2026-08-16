namespace AzureBuddy.Core.Agent;

/// <summary>
/// Fallback IChatHistoryStore used whenever Redis isn't configured - `dotnet run` and every
/// WebApplicationFactory-based integration test that doesn't opt into a Redis container land here. The
/// per-process shared state lives in InMemoryChatWindowCache (a singleton); this class itself is Scoped
/// so it can depend on the Scoped IChatHistorySource (backed by AppDbContext) without DI gymnastics.
/// </summary>
public sealed class InMemoryChatHistoryStore : IChatHistoryStore
{
    private readonly InMemoryChatWindowCache _cache;
    private readonly IChatHistorySource _source;

    public InMemoryChatHistoryStore(InMemoryChatWindowCache cache, IChatHistorySource source)
    {
        _cache = cache;
        _source = source;
    }

    public async Task<ChatSessionWindow> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGet(sessionId, out var cached))
        {
            return new ChatSessionWindow(sessionId, cached!, Guid.NewGuid().ToString());
        }

        var rehydrated = await _source.LoadRecentAsync(sessionId, ChatSessionWindow.DefaultWindowSize, cancellationToken);
        return new ChatSessionWindow(sessionId, rehydrated, Guid.NewGuid().ToString());
    }

    public Task SaveAsync(ChatSessionWindow window, CancellationToken cancellationToken = default)
    {
        _cache.Set(window.SessionId, window.Messages);
        return Task.CompletedTask;
    }

    public Task EvictAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _cache.Remove(sessionId);
        return Task.CompletedTask;
    }
}
