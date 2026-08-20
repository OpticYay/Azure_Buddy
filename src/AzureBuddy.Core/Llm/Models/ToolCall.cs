namespace AzureBuddy.Core.Llm.Models;

/// <summary>A tool invocation requested by the model. ArgumentsJson is the raw JSON object the model produced
/// for the tool's parameters (matches the tool's ParametersSchema).</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }

    /// <summary>Gemini-only: the opaque signature Gemini 3 returns alongside a functionCall part and
    /// requires echoed back on that same part when the call is replayed into history (e.g. the next
    /// tool-calling round in AzureBuddyAgent's loop) - omitting it is a 400 INVALID_ARGUMENT. Null for
    /// every other provider and for models that don't emit one.</summary>
    public string? GeminiThoughtSignature { get; init; }
}
