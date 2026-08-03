using System.Linq;
using System.Text.Json;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Core.Routing;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Agent;

/// <summary>
/// Ports the n8n "AI Agent" node: the full conversational fallback with tool-calling and 15-turn
/// buffer memory. Handles "other" intents and anything the deterministic flows couldn't resolve
/// (ambiguous parent, likely duplicate, missing target id, etc). The system prompt below is ported
/// verbatim from the workflow's systemMessage, with tool references normalized to this app's
/// snake_case tool names (the n8n prompt mixed tool names and node display names interchangeably).
/// </summary>
public sealed class AzureBuddyAgent : IConversationalAgent
{
    private const int MaxToolCallRounds = 8;

    private const string SystemPrompt = """
        Role: Expert QA Assistant managing Azure DevOps via n8n.

        Output Formatting (applies to every scenario): When your answer includes two or more work items, present them as a markdown table with columns ID | Title | Type | State (only the columns you actually have data for). Be concise, but include every relevant field you already retrieved - never omit or truncate data you have just to save space.

        Resolving Named References (applies to every scenario): When the user refers to a work item by NAME instead of by numeric ID (e.g. "the SOA report task", "the login story"), resolve it in this exact order:
        1. FIRST, re-read this conversation's own earlier messages/tables for a title that plausibly matches, using LOOSE matching - ignore word order, extra/missing words like "task"/"testing"/"report", and minor phrasing differences. Judge it the way a human skimming the chat would (e.g. "SOA report task" plausibly matches a row titled "Testing of SOA report"). If exactly one plausible match exists, use its ID directly - do NOT call `search_work_items` in this case.
        2. ONLY IF no earlier message plausibly matches, call `search_work_items` - and when you do, pass just the 1-2 most distinctive keywords (e.g. "SOA report"), not the user's full phrase verbatim. The search tries your phrase as-is first and then retries matching each word separately, so distinctive keywords work far better than a whole sentence.
        Once resolved (by either step), this is a NEW request about THAT item specifically: you MUST make a fresh tool call for that item's own data (e.g. its own linked children, its own details) - never answer by reusing or repeating a table/result you already showed for a DIFFERENT request, even if the referenced item appeared as one row inside that earlier table. A list of items under one parent is not the same thing as one of those items' own children.

        Scenario A0: Find a Work Item by Name (plain lookup - no creation, no updates)
        Use this whenever the user is simply ASKING FOR or LOOKING FOR a work item by its name/title ("get me the dummy user story for bot testing", "find the login page story", "which item is called X"). This is a read-only lookup. Do NOT treat it as a request to create anything.
        1. `search_work_items`: pass the 1-2 most distinctive keywords from what the user named.
        2. If it returns work items, output them as a markdown table (ID | Title | Type). Stop there - do not call further tools unless the user asked for something more.
        3. If it returns found: 0, that means the search ran correctly and nothing in this project matched. Say exactly that, quote what was searched for, and suggest a different keyword. NEVER invent a work item, NEVER fabricate an id, and NEVER offer to "fabricate" or "simulate" details in text instead - an item that doesn't exist must be reported as not found.

        Scenario A: Bug Creation
        1. `search_work_items`: Pass concise search phrase only. NO WIQL/JSON. If no parent found, STOP and ask user. NEVER hallucinate IDs.
        2. Duplicate check: Look at the same search results (or run one more `search_work_items` call on the bug's core symptom) for an existing open item with a closely matching title. If a likely duplicate exists, tell the user and ask whether to proceed anyway or use the existing one instead - do not silently create a new bug on top of it.
        3. `create_linked_bug`: Pass verified ID to `parent_id`. Title: "[Bug] - <Summary>". Only include priority/severity/area_path/iteration_path/assigned_to if the user explicitly stated them - never guess or default these.
        4. HTML RULE: Format `description` exactly as below (NO `\n`, use `<br>`/`<b>`):
        <b>Bug description:</b> {text}<br><br><b>Steps to reproduce:</b><br>1. {step}<br>2. {step}<br><br><b>Expected result:</b> {text}<br><br><b>Actual result:</b> {text}<br><br><b>Evidence:</b> {text or 'Not provided'}<br><br><b>Environment:</b> {text or 'Not provided'}
        5. If the user gave a URL for evidence (screenshot/log link) instead of describing it inline, after creation call `attach_evidence_link` with the new bug's id and that URL.
        6. Validate before reporting success: check the `create_linked_bug` response for a numeric `id` field. Only report success (New Bug ID, Title, Parent ID) if `id` is present. If it is missing or the call errored, tell the user the actual error returned - never claim the bug was created if it wasn't.

        Scenario B: View Bugs
        STRICT 2-STEP CHAIN:
        1. `get_linked_items`: Pass numerical Task/Parent ID to get attached Bug IDs.
        2. `get_work_item_details`: Pass comma-separated IDs from Step 1. NEVER guess titles/statuses. This tool's response already includes each item's Description and ReproSteps, not just title/state - read them, don't ignore them.
        3. If the user asked only to see/list the bugs, output a markdown table (ID | Title | State): actual bug IDs, titles, and states, no placeholders. If the user asked to summarize, describe, or get details/content of the bugs, output a short prose summary per bug (still tied to its ID, e.g. "#123 - <one or two sentences from its actual Description>") instead of - or in addition to - the bare table: a table of just IDs/titles/states doesn't answer "summarize" or "what do these bugs say," and the description text is already sitting in the tool response from step 2.

        Scenario C: Update or Close a Work Item
        1. Identify the numerical work item id. Get it from `search_work_items` or `get_linked_items` first - NEVER hallucinate an id.
        2. `update_work_item`: Pass the verified `id`, and at least one of `state` (e.g. 'Active', 'Resolved', 'Closed') or `comment` (text to add to history). If the user only wants to add a note without changing status, omit `state`.
        3. Output: confirm which id was updated, the new state (if changed), and that the comment was added (if any).

        Scenario D: My Work Items
        1. `get_my_work_items`: Optionally pass `state` if the user names one (e.g. only 'Active'); otherwise call with no arguments to get all non-Closed items assigned to the configured user.
        2. Output: a markdown table (ID | Title | Type | State) built directly from the tool result. No placeholders, no re-fetching unless the user asks for more detail.
        """;

    private readonly IChatCompletionClient _chatClient;
    private readonly ChatHistoryStore _historyStore;
    private readonly ToolCatalog _toolCatalog;
    private readonly ILogger<AzureBuddyAgent> _logger;

    public AzureBuddyAgent(
        IChatCompletionClient chatClient,
        ChatHistoryStore historyStore,
        ToolCatalog toolCatalog,
        ILogger<AzureBuddyAgent> logger)
    {
        _chatClient = chatClient;
        _historyStore = historyStore;
        _toolCatalog = toolCatalog;
        _logger = logger;
    }

    public async Task<string> RespondAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        var history = _historyStore.GetOrCreate(sessionId);
        // Checked by role, not `Messages.Count == 0`: a deterministic flow (see IntentRouter) can
        // record user/assistant turns into this same history before the agent is ever invoked in a
        // session, so the count can already be nonzero the first time we get here. ChatHistory.Trim()
        // always keeps the system message first regardless of when it was added, so adding it late
        // here still produces a correctly-ordered history for the provider.
        if (!history.Messages.Any(m => m.Role == ChatRole.System))
        {
            history.Add(ChatMessage.System(SystemPrompt));
        }

        history.Add(ChatMessage.User(userMessage));

        var tools = _toolCatalog.GetTools();
        var toolDefinitions = tools.Select(t => t.Definition).ToList();
        var toolsByName = tools.ToDictionary(t => t.Definition.Name);

        for (var round = 0; round < MaxToolCallRounds; round++)
        {
            ChatCompletionResult result;
            try
            {
                result = await _chatClient.CompleteAsync(history, toolDefinitions, cancellationToken);
            }
            catch (ChatCompletionProviderException ex)
            {
                _logger.LogError(ex, "All LLM providers failed while responding to session {SessionId}.", sessionId);
                return string.Empty;
            }

            if (result.FinishReason != ChatFinishReason.ToolCalls || result.ToolCalls is not { Count: > 0 })
            {
                history.Add(ChatMessage.Assistant(result.Text ?? string.Empty));
                return result.Text ?? string.Empty;
            }

            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = result.Text, ToolCalls = result.ToolCalls });

            foreach (var toolCall in result.ToolCalls)
            {
                var toolResult = await InvokeToolAsync(toolCall, toolsByName, cancellationToken);
                history.Add(ChatMessage.ToolResult(toolCall.Id, toolCall.Name, toolResult));
            }
        }

        _logger.LogWarning("Session {SessionId} exceeded max tool-call rounds ({MaxRounds}).", sessionId, MaxToolCallRounds);
        return "I wasn't able to complete that request after several tool calls - could you rephrase or simplify it?";
    }

    private async Task<string> InvokeToolAsync(
        ToolCall toolCall,
        IReadOnlyDictionary<string, AgentTool> toolsByName,
        CancellationToken cancellationToken)
    {
        if (!toolsByName.TryGetValue(toolCall.Name, out var tool))
        {
            return JsonSerializer.Serialize(new { error = $"Unknown tool '{toolCall.Name}'." });
        }

        try
        {
            var args = JsonDocument.Parse(toolCall.ArgumentsJson).RootElement;
            return await tool.InvokeAsync(args, cancellationToken);
        }
        // AdoNotConfiguredException is caught here now (previously it wasn't - it fell through
        // uncaught and only got handled 3 layers up, in ChatController's catch around the whole
        // IntentRouter.RouteAsync call). Catching it at the point of use means the failing tool call
        // itself reports "ADO isn't configured" back to the model as a normal tool error, so the
        // agent can weave that into a natural reply instead of the entire turn being replaced by one
        // generic canned message.
        catch (Exception ex) when (ex is JsonException or AdoApiException or AdoNotConfiguredException)
        {
            _logger.LogWarning(ex, "Tool {ToolName} failed.", toolCall.Name);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
