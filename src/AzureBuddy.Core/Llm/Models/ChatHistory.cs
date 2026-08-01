namespace AzureBuddy.Core.Llm.Models;

/// <summary>Ordered conversation turns, capped to a sliding window (mirrors n8n's Simple Memory bufferWindow, default 15).</summary>
public sealed class ChatHistory
{
    private readonly List<ChatMessage> _messages = new();
    public int WindowSize { get; }

    public ChatHistory(int windowSize = 15)
    {
        WindowSize = windowSize;
    }

    /// <summary>Messages currently in the window, oldest first. System message (if any) is always retained regardless of window size.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Add(ChatMessage message)
    {
        _messages.Add(message);
        Trim();
    }

    public void AddRange(IEnumerable<ChatMessage> messages)
    {
        _messages.AddRange(messages);
        Trim();
    }

    private void Trim()
    {
        var systemMessages = _messages.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystem = _messages.Where(m => m.Role != ChatRole.System).ToList();

        if (nonSystem.Count > WindowSize)
        {
            nonSystem = nonSystem.Skip(nonSystem.Count - WindowSize).ToList();
        }

        _messages.Clear();
        _messages.AddRange(systemMessages);
        _messages.AddRange(nonSystem);
    }
}
