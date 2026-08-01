namespace AzureBuddy.Core.Llm.Models;

/// <summary>A tool invocation requested by the model. ArgumentsJson is the raw JSON object the model produced
/// for the tool's parameters (matches the tool's ParametersSchema).</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
}
