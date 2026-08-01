namespace AzureBuddy.Core.Intent;

/// <summary>
/// Mirrors the JSON object produced by the n8n "Extract Intent" node and cleaned up by "Parse Extraction":
/// a single-turn, memoryless structured read of the user's latest message. Never invent values here -
/// every field defaults to empty/None exactly like the n8n Code node's fallbacks.
/// </summary>
public sealed record ExtractedIntent
{
    public required ChatIntent Intent { get; init; }
    public string ParentSearchTerm { get; init; } = string.Empty;
    public string WorkItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<string> ReproSteps { get; init; } = Array.Empty<string>();
    public string ExpectedResult { get; init; } = string.Empty;
    public string ActualResult { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string AreaPath { get; init; } = string.Empty;
    public string IterationPath { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;

    public static ExtractedIntent Fallback() => new() { Intent = ChatIntent.Other };
}
