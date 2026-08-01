namespace AzureBuddy.Core.Llm.Models;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>Provider-agnostic chat message. Providers translate this to/from their own wire format.</summary>
public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public string? Content { get; init; }

    /// <summary>Populated on an Assistant message when the model requested tool calls instead of (or alongside) text.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Populated on a Tool message: the id of the ToolCall this is the result of (matches
    /// ToolCall.Id from the preceding Assistant message). Providers that match by id (OpenAI-style) use this.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Populated on a Tool message: the function name this is the result of. Providers that match
    /// by name instead of id (Gemini) use this.</summary>
    public string? ToolName { get; init; }

    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };
    public static ChatMessage User(string content) => new() { Role = ChatRole.User, Content = content };
    public static ChatMessage Assistant(string content) => new() { Role = ChatRole.Assistant, Content = content };
    public static ChatMessage ToolResult(string toolCallId, string toolName, string content) =>
        new() { Role = ChatRole.Tool, Content = content, ToolCallId = toolCallId, ToolName = toolName };
}
