using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Intent;

/// <summary>
/// Ports the n8n "Extract Intent" + "Parse Extraction" node pair: a cheap, memoryless, single-turn LLM
/// call that classifies the user's latest message into one of the deterministic intents (or "other"),
/// so the router can skip the full conversational agent for common, well-formed requests. Deliberately
/// stateless - it sees only the current message, never prior turns, matching the n8n prompt's own note
/// that it "has no memory of earlier turns."
/// </summary>
public sealed class IntentExtractor
{
    private const string SystemPrompt = """
        You are a silent data-extraction step, not a conversational assistant. You only see the user's LATEST message in isolation - you have no memory of earlier turns. Output ONLY a single-line minified JSON object matching the schema below - no prose, no markdown, no code fences, nothing else.

        Schema:
        {"intent": "create_bug" | "view_bugs" | "update_item" | "my_items" | "prioritize_work_items" | "other", "parent_search_term": string, "work_item_id": string, "title": string, "repro_steps": array of strings, "expected_result": string, "actual_result": string, "evidence": string, "environment": string, "priority": string, "severity": string, "area_path": string, "iteration_path": string, "assigned_to": string, "state": string, "comment": string, "work_item_type_filter": string, "has_additional_request": boolean, "is_urgent": boolean}

        Intent rules:
        - "create_bug": the message clearly asks to file/create/log a new bug AND includes at least a title/summary and some description of the problem.
        - "view_bugs": the message asks for a plain LIST of the bugs/tasks linked to a parent work item (e.g. "what bugs are under #123", "show me bugs linked to 123") AND includes a specific numeric work item id in `work_item_id`. If only a name/description is given (no numeric id), use "other" instead. If the message asks for anything beyond a bare list - summarizing, describing, explaining, or getting the full details/content of those bugs - use "other" instead, even though it also mentions a numeric id: this fast path can only ever return an ID/Title/Type/State table, never each bug's actual description, so a request that needs the description has to go to the fuller tool-calling path instead.
        - "update_item": the message clearly asks to change the state and/or add a comment to a specific work item AND includes a specific numeric id in `work_item_id` AND at least one of `state` or `comment`. If no numeric id is given, use "other" instead.
        - "my_items": the message clearly asks to see work items assigned to the user, as a plain list, with no ranking/prioritization implied. `state` may optionally hold a single status to filter by. `work_item_type_filter` may optionally hold one work item type to filter by (see below).
        - "prioritize_work_items": the message asks which of the user's own assigned items to work on first/next, or which are most urgent/overdue/pressing (e.g. "what should I work on first", "what's most urgent", "prioritize my work items", "what should I do next"). `state` and `work_item_type_filter` may optionally filter it, same as my_items. Use "my_items" instead if the user just wants a plain list with no notion of priority/order.
        - "other": anything else, anything incomplete, or anything ambiguous.

        work_item_type_filter: only set for my_items/prioritize_work_items, when the user names a specific work item TYPE they want the list narrowed to - e.g. "bugs assigned to me", "what tasks do I have", "my user stories" all set this. Normalize to the canonical singular ADO type name: "bug"/"bugs" -> "Bug", "task"/"tasks" -> "Task", "user story"/"user stories"/"story"/"stories" -> "User Story", "feature"/"features" -> "Feature", "epic"/"epics" -> "Epic". Leave empty when the user just says "work items"/"items"/"my stuff" with no specific type named - that means every type, not a type called "work item".

        CRITICAL - do not guess on short or context-dependent replies: if the message is short, terse, or looks like it's answering a question you can't see (e.g. a bare number like "12312", "yes", "the second one", "active") rather than a complete standalone request, you MUST classify it as "other" even if it technically matches one of the patterns above (e.g. a bare number could satisfy view_bugs's numeric-id requirement, but is far more likely to be answering a prior clarifying question than a new view request). Only classify a specific intent when the message clearly, on its own, states a complete new request. When genuinely uncertain, always prefer "other" - a more capable assistant with full conversation memory will handle it correctly.

        has_additional_request: true if the message asks for more than the ONE thing described by `intent` above - e.g. it names two distinct actions ("file a bug for the login crash and show me my open items"), or asks about two unrelated work items. false if it's a single, self-contained request even if long. When true, set it regardless of what `intent` ends up being - this only flags that there's more in the message than a single deterministic action can cover.

        is_urgent: true only if the wording itself signals urgency or business impact - "urgent", "ASAP", "production is down", "blocking the release", "critical", "customers are affected" and similar. false for a routine request, even one about a high-severity-sounding bug, unless the user's own words convey urgency. Never infer urgency from a Priority/Severity field value alone - those are separate, explicit ADO fields, not this signal.

        General rules:
        - Never invent values. Use an empty string (or empty array for repro_steps) for anything the message did not provide.
        - Output must be valid JSON and nothing else - no leading or trailing text.
        """;

    private static readonly Regex JsonObjectPattern = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    private readonly IChatCompletionClient _chatClient;
    private readonly ILogger<IntentExtractor> _logger;

    public IntentExtractor(IChatCompletionClient chatClient, ILogger<IntentExtractor> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<ExtractedIntent> ExtractAsync(string latestUserMessage, CancellationToken cancellationToken = default)
    {
        var history = new ChatHistory(windowSize: 2);
        history.Add(ChatMessage.System(SystemPrompt));
        history.Add(ChatMessage.User(latestUserMessage));

        ChatCompletionResult result;
        try
        {
            result = await _chatClient.CompleteAsync(history, Array.Empty<ToolDefinition>(), cancellationToken);
        }
        catch (ChatCompletionProviderException ex)
        {
            _logger.LogWarning(ex, "Intent extraction call failed; falling back to 'other'.");
            return ExtractedIntent.Fallback();
        }

        _logger.LogDebug("Intent extraction raw output for {Message}: {Raw}", latestUserMessage, result.Text);
        return Parse(result.Text);
    }

    private ExtractedIntent Parse(string? rawOutput)
    {
        var raw = rawOutput ?? string.Empty;
        var match = JsonObjectPattern.Match(raw);
        var jsonText = match.Success ? match.Value : raw;

        RawExtraction? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawExtraction>(jsonText, JsonSerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse intent extraction output: {Raw}", raw);
            return ExtractedIntent.Fallback();
        }

        if (parsed is null)
        {
            return ExtractedIntent.Fallback();
        }

        return new ExtractedIntent
        {
            Intent = ParseIntent(parsed.Intent),
            ParentSearchTerm = parsed.ParentSearchTerm ?? string.Empty,
            WorkItemId = TextUtils.DigitsOnly(parsed.WorkItemId),
            Title = parsed.Title ?? string.Empty,
            ReproSteps = (IReadOnlyList<string>?)parsed.ReproSteps ?? Array.Empty<string>(),
            ExpectedResult = parsed.ExpectedResult ?? string.Empty,
            ActualResult = parsed.ActualResult ?? string.Empty,
            Evidence = parsed.Evidence ?? string.Empty,
            Environment = parsed.Environment ?? string.Empty,
            Priority = parsed.Priority ?? string.Empty,
            Severity = parsed.Severity ?? string.Empty,
            AreaPath = parsed.AreaPath ?? string.Empty,
            IterationPath = parsed.IterationPath ?? string.Empty,
            AssignedTo = parsed.AssignedTo ?? string.Empty,
            State = parsed.State ?? string.Empty,
            Comment = parsed.Comment ?? string.Empty,
            WorkItemTypeFilter = parsed.WorkItemTypeFilter ?? string.Empty,
            HasAdditionalRequest = parsed.HasAdditionalRequest,
            IsUrgent = parsed.IsUrgent
        };
    }

    private static ChatIntent ParseIntent(string? value) => ChatIntentNames.Parse(value);

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed class RawExtraction
    {
        [JsonPropertyName("intent")] public string? Intent { get; set; }
        [JsonPropertyName("parent_search_term")] public string? ParentSearchTerm { get; set; }
        [JsonPropertyName("work_item_id")] public string? WorkItemId { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("repro_steps")] public List<string>? ReproSteps { get; set; }
        [JsonPropertyName("expected_result")] public string? ExpectedResult { get; set; }
        [JsonPropertyName("actual_result")] public string? ActualResult { get; set; }
        [JsonPropertyName("evidence")] public string? Evidence { get; set; }
        [JsonPropertyName("environment")] public string? Environment { get; set; }
        [JsonPropertyName("priority")] public string? Priority { get; set; }
        [JsonPropertyName("severity")] public string? Severity { get; set; }
        [JsonPropertyName("area_path")] public string? AreaPath { get; set; }
        [JsonPropertyName("iteration_path")] public string? IterationPath { get; set; }
        [JsonPropertyName("assigned_to")] public string? AssignedTo { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("comment")] public string? Comment { get; set; }
        [JsonPropertyName("work_item_type_filter")] public string? WorkItemTypeFilter { get; set; }
        [JsonPropertyName("has_additional_request")] public bool HasAdditionalRequest { get; set; }
        [JsonPropertyName("is_urgent")] public bool IsUrgent { get; set; }
    }
}
