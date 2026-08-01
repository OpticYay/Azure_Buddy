using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        {"intent": "create_bug" | "view_bugs" | "update_item" | "my_items" | "other", "parent_search_term": string, "work_item_id": string, "title": string, "repro_steps": array of strings, "expected_result": string, "actual_result": string, "evidence": string, "environment": string, "priority": string, "severity": string, "area_path": string, "iteration_path": string, "assigned_to": string, "state": string, "comment": string}

        Intent rules:
        - "create_bug": the message clearly asks to file/create/log a new bug AND includes at least a title/summary and some description of the problem.
        - "view_bugs": the message clearly asks to see bugs/tasks linked to a parent work item AND includes a specific numeric work item id in `work_item_id`. If only a name/description is given (no numeric id), use "other" instead.
        - "update_item": the message clearly asks to change the state and/or add a comment to a specific work item AND includes a specific numeric id in `work_item_id` AND at least one of `state` or `comment`. If no numeric id is given, use "other" instead.
        - "my_items": the message clearly asks to see work items assigned to the user. `state` may optionally hold a single status to filter by.
        - "other": anything else, anything incomplete, or anything ambiguous.

        CRITICAL - do not guess on short or context-dependent replies: if the message is short, terse, or looks like it's answering a question you can't see (e.g. a bare number like "12312", "yes", "the second one", "active") rather than a complete standalone request, you MUST classify it as "other" even if it technically matches one of the patterns above (e.g. a bare number could satisfy view_bugs's numeric-id requirement, but is far more likely to be answering a prior clarifying question than a new view request). Only classify a specific intent when the message clearly, on its own, states a complete new request. When genuinely uncertain, always prefer "other" - a more capable assistant with full conversation memory will handle it correctly.

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
            WorkItemId = DigitsOnly(parsed.WorkItemId),
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
            Comment = parsed.Comment ?? string.Empty
        };
    }

    private static ChatIntent ParseIntent(string? value) => value switch
    {
        "create_bug" => ChatIntent.CreateBug,
        "view_bugs" => ChatIntent.ViewBugs,
        "update_item" => ChatIntent.UpdateItem,
        "my_items" => ChatIntent.MyItems,
        _ => ChatIntent.Other
    };

    private static string DigitsOnly(string? value) =>
        value is null ? string.Empty : Regex.Replace(value, "[^0-9]", "");

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
    }
}
