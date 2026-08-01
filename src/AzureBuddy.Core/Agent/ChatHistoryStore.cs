using System.Collections.Concurrent;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Per-session conversation memory, windowed to the last 15 turns - mirrors the n8n
/// "Simple Memory" bufferWindow node (contextWindowLength: 15). In-memory only; swap for a
/// distributed cache (Redis) if the app ever runs across multiple instances.</summary>
public sealed class ChatHistoryStore
{
    private const int DefaultWindowSize = 15;
    private readonly ConcurrentDictionary<string, ChatHistory> _sessions = new();

    public ChatHistory GetOrCreate(string sessionId) =>
        _sessions.GetOrAdd(sessionId, _ => new ChatHistory(DefaultWindowSize));
}
