namespace AzureBuddy.Core.Llm.Models;

/// <summary>A tool invocation requested by the model. ArgumentsJson is the raw JSON object the model produced
/// for the tool's parameters (matches the tool's ParametersSchema).</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }

    /// <summary>Gemini-only: the opaque `thoughtSignature` Gemini attaches to a functionCall part on
    /// "thinking" models (e.g. gemini-3.x). Gemini rejects the next request with a 400 ("Function call
    /// is missing a thought_signature in functionCall parts") if this isn't echoed back verbatim on the
    /// same functionCall part when replaying history. Null/ignored for other providers.</summary>
    public string? GeminiThoughtSignature { get; init; }
}
