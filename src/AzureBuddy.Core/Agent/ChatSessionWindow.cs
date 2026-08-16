using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>
/// A per-turn, non-shared unit of work wrapping a ChatHistory - the mutable-shared-object contract
/// ChatHistoryStore.GetOrCreate used to hand out (mutate in place, no write-back) can't cross a network,
/// so this replaces it. Because IChatCompletionClient.CompleteAsync still takes the plain ChatHistory
/// (see Messages/ToChatHistory), neither provider client needed to change for this to exist.
///
/// Tracks two extra things a plain ChatHistory doesn't need to: `Appended` (what THIS turn added, used
/// by CollapseFailedTurn to undo a dead tool-calling chain without losing anything from before the
/// turn started) and `Version` (an opaque token a store can use for optimistic concurrency - unused by
/// the in-memory store, load-bearing for the Redis one).
/// </summary>
public sealed class ChatSessionWindow
{
    public const int DefaultWindowSize = 15;

    private readonly IReadOnlyList<ChatMessage> _baseMessages;
    private readonly int _windowSize;
    private readonly List<ChatMessage> _appended = new();
    private ChatHistory _history;

    public string SessionId { get; }

    /// <summary>Opaque concurrency token from the store this window was loaded from/for. Two windows
    /// for the same session loaded at different times carry different values; a store that supports
    /// compare-and-swap (see RedisChatHistoryStore) uses this to detect a lost race on save.</summary>
    public string Version { get; }

    public IReadOnlyList<ChatMessage> Messages => _history.Messages;

    /// <summary>What this turn itself added, oldest first - used by CollapseFailedTurn to identify the
    /// user's own message versus the tool-calling scaffolding that needs to be rolled back.</summary>
    public IReadOnlyList<ChatMessage> Appended => _appended;

    public ChatSessionWindow(string sessionId, IReadOnlyList<ChatMessage> messages, string version, int windowSize = DefaultWindowSize)
    {
        SessionId = sessionId;
        Version = version;
        _windowSize = windowSize;
        _baseMessages = messages;
        _history = new ChatHistory(windowSize);
        _history.AddRange(messages);
    }

    public void Add(ChatMessage message)
    {
        _history.Add(message);
        _appended.Add(message);
    }

    /// <summary>Adds a user turn unless the window already ends with a User message of identical
    /// content, in which case it's a no-op. Needed because ChatController.PostAsync persists the user's
    /// message to MySQL BEFORE calling IntentRouter.RouteAsync - so on a cache miss, DbChatHistorySource
    /// rehydrates a window whose last row already IS the message about to be added again. Without this
    /// guard, every miss would duplicate the user's own message in the model's context.</summary>
    public void AddUserTurn(ChatMessage userMessage)
    {
        var last = _history.Messages.Count > 0 ? _history.Messages[^1] : null;
        if (last is { Role: ChatRole.User } && last.Content == userMessage.Content)
        {
            return;
        }

        Add(userMessage);
    }

    /// <summary>Called when a tool-calling turn exhausts MaxToolCallRounds without ever producing a
    /// final reply: rewinds to the state before this turn started, re-adds only the user's own message,
    /// then appends the canned apology as a plain assistant reply - so the dead-end tool-call chain
    /// (up to 8 rounds of Assistant-with-ToolCalls + Tool-result pairs) doesn't occupy the whole 15-slot
    /// window going forward.</summary>
    public void CollapseFailedTurn(string cannedReply)
    {
        var userTurn = _appended.FirstOrDefault(m => m.Role == ChatRole.User)
            ?? _history.Messages.LastOrDefault(m => m.Role == ChatRole.User)
            ?? throw new InvalidOperationException(
                "CollapseFailedTurn requires a user turn to already be present in this window.");

        _history = new ChatHistory(_windowSize);
        _history.AddRange(_baseMessages);
        _history.Add(userTurn);
        var assistantReply = ChatMessage.Assistant(cannedReply);
        _history.Add(assistantReply);

        _appended.Clear();
        _appended.Add(userTurn);
        _appended.Add(assistantReply);
    }

    /// <summary>The plain ChatHistory a IChatCompletionClient actually consumes - callers never need to
    /// know this window wraps one.</summary>
    public ChatHistory ToChatHistory() => _history;
}
