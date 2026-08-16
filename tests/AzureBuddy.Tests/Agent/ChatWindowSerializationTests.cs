using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Llm.Models;
using Xunit;

namespace AzureBuddy.Tests.Agent;

public class ChatWindowSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesRoleContentToolCallsAndToolIds()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("you are a helpful assistant"),
            ChatMessage.User("list my open bugs"),
            new()
            {
                Role = ChatRole.Assistant,
                Content = null,
                ToolCalls = new List<ToolCall>
                {
                    new() { Id = "call_1", Name = "list_work_items", ArgumentsJson = "{\"type\":\"Bug\"}" },
                },
            },
            ChatMessage.ToolResult("call_1", "list_work_items", "[{\"id\":123}]"),
            ChatMessage.Assistant("You have 1 open bug: #123."),
        };

        var json = ChatWindowSerialization.Serialize(messages);
        var roundTripped = ChatWindowSerialization.Deserialize(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(messages.Count, roundTripped!.Count);

        for (var i = 0; i < messages.Count; i++)
        {
            Assert.Equal(messages[i].Role, roundTripped[i].Role);
            Assert.Equal(messages[i].Content, roundTripped[i].Content);
            Assert.Equal(messages[i].ToolCallId, roundTripped[i].ToolCallId);
            Assert.Equal(messages[i].ToolName, roundTripped[i].ToolName);
        }

        var assistantWithToolCalls = roundTripped[2];
        Assert.NotNull(assistantWithToolCalls.ToolCalls);
        Assert.Single(assistantWithToolCalls.ToolCalls!);
        Assert.Equal("call_1", assistantWithToolCalls.ToolCalls![0].Id);
        Assert.Equal("list_work_items", assistantWithToolCalls.ToolCalls![0].Name);
        Assert.Equal("{\"type\":\"Bug\"}", assistantWithToolCalls.ToolCalls![0].ArgumentsJson);
    }

    [Fact]
    public void Serialize_UsesCamelCaseStringEnumsForRole()
    {
        var json = ChatWindowSerialization.Serialize(new[] { ChatMessage.User("hi") });

        Assert.Contains("\"role\":\"user\"", json);
        Assert.DoesNotContain("\"Role\":0", json);
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsNull()
    {
        var result = ChatWindowSerialization.Deserialize("{not valid json");

        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_MissingRequiredRoleMember_ReturnsNull()
    {
        // "content" only, no "role" - Role is a `required` member on ChatMessage, so STJ should throw
        // rather than silently default it, and Deserialize should turn that into a cache miss.
        var json = """{"schemaVersion":1,"messages":[{"content":"hi"}]}""";

        var result = ChatWindowSerialization.Deserialize(json);

        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_UnknownSchemaVersion_ReturnsNull()
    {
        var json = """{"schemaVersion":999,"messages":[{"role":"user","content":"hi"}]}""";

        var result = ChatWindowSerialization.Deserialize(json);

        Assert.Null(result);
    }
}
