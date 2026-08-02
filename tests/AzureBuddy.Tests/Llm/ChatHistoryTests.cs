using AzureBuddy.Core.Llm.Models;
using Xunit;

namespace AzureBuddy.Tests.Llm;

public class ChatHistoryTests
{
    [Fact]
    public void Add_WithinWindow_KeepsAllMessages()
    {
        var history = new ChatHistory(windowSize: 5);

        history.Add(ChatMessage.User("one"));
        history.Add(ChatMessage.Assistant("two"));
        history.Add(ChatMessage.User("three"));

        Assert.Equal(3, history.Messages.Count);
    }

    [Fact]
    public void Add_BeyondWindow_TrimsOldestNonSystemMessages()
    {
        var history = new ChatHistory(windowSize: 3);

        for (var i = 1; i <= 5; i++)
        {
            history.Add(ChatMessage.User($"message-{i}"));
        }

        Assert.Equal(3, history.Messages.Count);
        Assert.Equal("message-3", history.Messages[0].Content);
        Assert.Equal("message-4", history.Messages[1].Content);
        Assert.Equal("message-5", history.Messages[2].Content);
    }

    [Fact]
    public void Add_SystemMessage_IsNeverTrimmedRegardlessOfWindowSize()
    {
        var history = new ChatHistory(windowSize: 2);

        history.Add(ChatMessage.System("you are a helpful assistant"));
        for (var i = 1; i <= 5; i++)
        {
            history.Add(ChatMessage.User($"message-{i}"));
        }

        Assert.Contains(history.Messages, m => m.Role == ChatRole.System);
        // System message + the 2-message window of non-system messages.
        Assert.Equal(3, history.Messages.Count);
    }

    [Fact]
    public void Add_SystemMessage_AlwaysOrderedBeforeNonSystemMessages()
    {
        var history = new ChatHistory(windowSize: 5);

        history.Add(ChatMessage.User("first user message"));
        history.Add(ChatMessage.System("system prompt added later"));

        Assert.Equal(ChatRole.System, history.Messages[0].Role);
    }

    [Fact]
    public void AddRange_TrimsToWindowJustLikeSequentialAdds()
    {
        var history = new ChatHistory(windowSize: 2);

        history.AddRange(new[]
        {
            ChatMessage.User("a"),
            ChatMessage.Assistant("b"),
            ChatMessage.User("c")
        });

        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("b", history.Messages[0].Content);
        Assert.Equal("c", history.Messages[1].Content);
    }
}
