namespace AzureBuddy.Core.Llm.Models;

public enum ChatFinishReason
{
    Stop,
    ToolCalls,
    Length,
    Error
}

public sealed class ChatCompletionResult
{
    public required ChatFinishReason FinishReason { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Which provider actually produced this result (useful for logging/telemetry when running behind a fallback chain).</summary>
    public required string ProviderName { get; init; }
}
