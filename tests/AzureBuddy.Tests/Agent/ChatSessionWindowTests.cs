using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Llm.Models;
using Xunit;

namespace AzureBuddy.Tests.Agent;

public class ChatSessionWindowTests
{
    [Fact]
    public void AddUserTurn_WhenWindowAlreadyEndsWithSameUserMessage_DoesNotDuplicate()
    {
        // ChatController.PostAsync persists the user's message to MySQL BEFORE calling
        // IntentRouter.RouteAsync, so a window rehydrated from a cache miss can already end with this
        // exact message. Without this guard, every miss would duplicate it in the model's context.
        var window = new ChatSessionWindow("session-1", new[] { ChatMessage.User("hello") }, version: "v1");

        window.AddUserTurn(ChatMessage.User("hello"));

        Assert.Single(window.Messages);
    }

    [Fact]
    public void AddUserTurn_WhenLastMessageIsDifferentContent_Adds()
    {
        var window = new ChatSessionWindow("session-1", new[] { ChatMessage.User("hello") }, version: "v1");

        window.AddUserTurn(ChatMessage.User("a follow-up"));

        Assert.Equal(2, window.Messages.Count);
        Assert.Equal("a follow-up", window.Messages[^1].Content);
    }

    [Fact]
    public void AddUserTurn_WhenLastMessageIsAssistant_Adds()
    {
        var window = new ChatSessionWindow(
            "session-1", new[] { ChatMessage.User("hello"), ChatMessage.Assistant("hi there") }, version: "v1");

        window.AddUserTurn(ChatMessage.User("another question"));

        Assert.Equal(3, window.Messages.Count);
    }

    [Fact]
    public void CollapseFailedTurn_DropsToolScaffoldingButKeepsUserTurn()
    {
        var window = new ChatSessionWindow("session-1", messages: Array.Empty<ChatMessage>(), version: "v1");
        window.AddUserTurn(ChatMessage.User("file a bug"));

        // Simulate several dead tool-calling rounds - an Assistant-with-ToolCalls plus its Tool result,
        // repeated, none of which ever produced a final reply.
        for (var i = 0; i < 3; i++)
        {
            window.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = new List<ToolCall> { new() { Id = $"call_{i}", Name = "search_work_items", ArgumentsJson = "{}" } },
            });
            window.Add(ChatMessage.ToolResult($"call_{i}", "search_work_items", "[]"));
        }

        window.CollapseFailedTurn("I wasn't able to complete that request after several tool calls.");

        Assert.Equal(2, window.Messages.Count);
        Assert.Equal(ChatRole.User, window.Messages[0].Role);
        Assert.Equal("file a bug", window.Messages[0].Content);
        Assert.Equal(ChatRole.Assistant, window.Messages[1].Role);
        Assert.Equal("I wasn't able to complete that request after several tool calls.", window.Messages[1].Content);
        Assert.DoesNotContain(window.Messages, m => m.ToolCalls is not null);
        Assert.DoesNotContain(window.Messages, m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public void CollapseFailedTurn_PreservesMessagesFromBeforeThisTurn()
    {
        var priorMessages = new[]
        {
            ChatMessage.System("system prompt"),
            ChatMessage.User("earlier question"),
            ChatMessage.Assistant("earlier answer"),
        };
        var window = new ChatSessionWindow("session-1", priorMessages, version: "v1");
        window.AddUserTurn(ChatMessage.User("a new request that will fail"));
        window.Add(new ChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls = new List<ToolCall> { new() { Id = "call_1", Name = "search_work_items", ArgumentsJson = "{}" } },
        });
        window.Add(ChatMessage.ToolResult("call_1", "search_work_items", "[]"));

        window.CollapseFailedTurn("canned apology");

        Assert.Equal(5, window.Messages.Count);
        Assert.Equal("system prompt", window.Messages[0].Content);
        Assert.Equal("earlier question", window.Messages[1].Content);
        Assert.Equal("earlier answer", window.Messages[2].Content);
        Assert.Equal("a new request that will fail", window.Messages[3].Content);
        Assert.Equal("canned apology", window.Messages[4].Content);
    }
}
