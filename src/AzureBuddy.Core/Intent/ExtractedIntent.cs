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

    /// <summary>
    /// The ADO work item type (e.g. "Bug", "Task", "User Story") the user wants a my_items/
    /// prioritize_work_items list filtered to - e.g. "bugs assigned to me" should only return Bugs, not
    /// everything assigned to the user. Empty means no type filter (every type), matching the field's
    /// general "never invent, empty means not stated" convention.
    /// </summary>
    public string WorkItemTypeFilter { get; init; } = string.Empty;

    /// <summary>
    /// True when the message asks for more than the single thing <see cref="Intent"/> captures (e.g.
    /// "file a bug for X and also show my open items"). The deterministic flows can only ever act on
    /// one request, so the router treats this as a signal to skip them and go straight to the full
    /// conversational agent rather than silently answering one half and dropping the other.
    /// </summary>
    public bool HasAdditionalRequest { get; init; }

    /// <summary>
    /// Urgency the message itself conveys ("production is down", "urgent", "blocking release"),
    /// independent of any explicit ADO Priority/Severity field the user may or may not have stated.
    /// Never invented beyond what the wording supports - "normal" is the default, not a guess.
    /// </summary>
    public bool IsUrgent { get; init; }

    public static ExtractedIntent Fallback() => new() { Intent = ChatIntent.Other };
}
