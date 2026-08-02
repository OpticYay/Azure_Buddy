using System.Text.Json;

namespace AzureBuddy.Core.Llm.Models;

/// <summary>Provider-agnostic tool/function declaration. JsonSchema follows the standard JSON Schema
/// object shape (as used by OpenAI/Gemini/Ollama function-calling) so each provider can translate it directly.</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement ParametersSchema { get; init; }
}
