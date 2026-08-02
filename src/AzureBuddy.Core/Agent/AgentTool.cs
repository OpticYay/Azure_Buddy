using System.Text.Json;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Pairs a provider-agnostic ToolDefinition with the delegate that actually executes it.
/// The delegate receives the model's raw arguments (parsed as JsonElement, matching the tool's
/// ParametersSchema) and returns a string result that gets fed back to the model as a tool message.</summary>
public sealed class AgentTool
{
    public required ToolDefinition Definition { get; init; }
    public required Func<JsonElement, CancellationToken, Task<string>> InvokeAsync { get; init; }
}
