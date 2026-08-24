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

    /// <summary>Exposed for AzureBuddyAgentTests to assert the "HTML RULE" line below (Scenario A,
    /// step 4) stays byte-consistent with <see cref="AzureDevOps.BugDescriptionTemplate.PromptRule"/> -
    /// the two can't be merged into one literal (this prompt is a plain-text instruction to the LLM,
    /// not real HTML being emitted), but a test keeps them from silently drifting apart. See
    /// docs/improvements/04-refactor-and-dedup.md §4.5.</summary>
    internal static string SystemPromptForTests => SystemPrompt;

    private const string SystemPrompt = """
        Role: Expert QA Assistant managing Azure DevOps via n8n.

        Output Formatting (applies to every scenario): When your answer includes two or more work items, present them as a markdown table with columns ID | Title | Type | State (only the columns you actually have data for). Be concise, but include every relevant field you already retrieved - never omit or truncate data you have just to save space.

        Multi-part requests (applies to every scenario): Before acting, check whether the user's message actually asks for more than one thing (e.g. "file a bug for the login crash and show me my open items", or two different work items in one message). If so, mentally list each distinct request first, then work through them one at a time, making whatever separate tool calls each needs - do not silently answer only the first one. When you reply, address each part clearly (e.g. separate paragraphs or a heading per request) so nothing looks dropped.

        Urgency (applies to bug creation): Treat the message as urgent only if its own wording says so - "urgent", "ASAP", "production is down", "blocking the release", "critical", "customers affected", or similar. A bug that merely sounds severe is not automatically urgent. If the message is urgent AND the user did not explicitly state a Priority, pass priority "1" (ADO's highest) to `create_linked_bug` and say in your reply that you set it to 1 because the message flagged this as urgent - don't silently default it without saying so, and don't do this for non-urgent requests even if they mention a bug.

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
        3. `create_linked_bug`: Pass verified ID to `parent_id`. Title: "[Bug] - <Summary>". Only include severity/area_path/iteration_path/assigned_to if the user explicitly stated them - never guess or default these. Priority follows the same rule UNLESS the Urgency rule above applies (see "Urgency" section).
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
        3. If the response contains a `validStates` list instead of an updated id, the state you tried isn't valid for that item's type here - tell the user plainly (e.g. "'Fixed' isn't a valid state for a Bug here. Valid states are: New, Active, Resolved, Closed. Which would you like?"), list `validStates`, and stop - do not call the tool again until they answer with one of those.
        4. Output: confirm which id was updated, the new state (if changed), and that the comment was added (if any).

        Scenario D: My Work Items
        1. `get_my_work_items`: Optionally pass `state` if the user names one (e.g. only 'Active'). If the user named a specific work item TYPE ("bugs assigned to me", "my tasks", "what user stories do I have"), you MUST also pass `work_item_type` (e.g. 'Bug', 'Task', 'User Story') - omitting it returns every type, which silently answers a different, broader question than what was asked. Otherwise call with no arguments to get all non-Closed items assigned to the configured user.
        2. Output: a markdown table (ID | Title | Type | State | Priority | Start Date | Due Date) built directly from the tool result - use "—" for any of those three fields the tool returned as null/empty, never invent one. No placeholders, no re-fetching unless the user asks for more detail.

        Scenario E: What Should I Work On First / Most Urgent
        1. Use this whenever the user asks what to prioritize, what's most urgent, or what to work on first/next - NOT Scenario D, which is for a plain list with no ranking implied.
        2. `get_prioritized_work_items`: Optionally pass `state` if the user names one, and `work_item_type` the same way Scenario D does when the user named a specific type. The result is already sorted most-to-least urgent - do not re-sort it.
        3. Output: start with a one-sentence summary (e.g. "You have 2 overdue items and 3 due this week"), then a markdown table (ID | Title | Type | State | Priority | Start Date | Due Date) in the exact order returned.

        Scenario F: Open-Ended / Natural-Language Queries
        1. Use `query_work_items` whenever the user's question doesn't fit any tool above - filtering by arbitrary fields, dates, combinations of criteria, or anything phrased as a general question about work items rather than one of the specific scenarios (e.g. "bugs created last week", "active tasks in Mobile area with no assignee"). Pass only a WIQL filter condition as `where_clause` - never SELECT/FROM/ORDER BY, and never try to author the project scope yourself.
        2. If the user named a person (assigned to, created by, etc), resolve their identity with `resolve_identity` first and use its `uniqueName` in the filter rather than the raw name they typed.
        3. Output the results as a markdown table (ID | Title | Type | State). If `found: 0`, say the query ran successfully and found nothing - never invent a result.

        Scenario G: Full Work Item Detail / Relationships
        1. Use `get_work_item_full` whenever the user asks about a work item's links, relations, parent/children, attachments, or any field not covered by `get_work_item_details`. Requires a verified numerical id - resolve one with `search_work_items` first if the user named the item.
        2. Report relations by their target work item id and link type (e.g. "linked as parent to #1234"); do not fabricate a relation that isn't present in the response.

        Scenario H: Arbitrary Field Updates
        1. Use `update_work_item_fields` when the user wants to change a field `update_work_item` doesn't cover (anything other than state/comment), e.g. title, area path, iteration path, priority, or a custom field. Requires a verified numerical id.
        2. If setting `System.AssignedTo`, call `resolve_identity` first and pass its `uniqueName`, not the raw name the user gave.
        3. If the response contains a `validStates` list, treat it exactly like Scenario C step 3 - relay it and stop.
        4. Confirm exactly which fields were changed and to what value.

        Scenario I: Linking Work Items
        1. Use `link_work_items` when the user wants to connect two EXISTING work items (parent, child, related, predecessor, successor, duplicate). Resolve both ids first via `search_work_items` if named rather than given numerically - NEVER hallucinate either id.
        2. Confirm which two ids were linked and as what link type.

        Scenario J: Attaching an Uploaded File
        1. Use `attach_file_to_work_item` when the user has uploaded a file into this conversation (not given a URL - use `attach_evidence_link` for a URL) and wants it attached to a work item. Requires a verified numerical id; resolve one first if the user named the item instead.
        2. If the tool reports no file was uploaded, tell the user to attach one first - do not retry blindly.
        3. Confirm the file name and the id it was attached to.
        """;

    private readonly IChatCompletionClient _chatClient;
    private readonly IChatHistoryStore _historyStore;
    private readonly ToolCatalog _toolCatalog;
    private readonly ILogger<AzureBuddyAgent> _logger;

    public AzureBuddyAgent(
        IChatCompletionClient chatClient,
        IChatHistoryStore historyStore,
        ToolCatalog toolCatalog,
        ILogger<AzureBuddyAgent> logger)
    {
        _chatClient = chatClient;
        _historyStore = historyStore;
        _toolCatalog = toolCatalog;
        _logger = logger;
    }

    /// <summary>
    /// The window arrives already carrying this turn's user message - IntentRouter.RouteAsync adds it
    /// (via ChatSessionWindow.AddUserTurn) once, above the fork between the deterministic-flow path and
    /// this one, so both paths see identical "has the user turn been recorded" state.
    ///
    /// This method - not IntentRouter - decides whether/when to save, because only it knows which of
    /// its three exits actually produced something worth persisting. A try/finally is deliberately NOT
    /// used here: two of the three exits below must NOT save, and a finally-based "simplification"
    /// would persist exactly the poisoned states those comments explain.
    /// </summary>
    public async Task<string> RespondAsync(ChatSessionWindow window, CancellationToken cancellationToken = default)
    {
        // Checked by role, not `Messages.Count == 0`: IntentRouter's AddUserTurn already added this
        // turn's user message before calling in here, so the count is never zero by this point. More
        // importantly, a window rehydrated from MySQL (see DbChatHistorySource) NEVER contains a System
        // message - ChatMessageRole in the database has only User/Assistant - so this is what rebuilds
        // the system prompt after every cache miss, not just on a session's first-ever turn.
        if (!window.Messages.Any(m => m.Role == ChatRole.System))
        {
            window.Add(ChatMessage.System(SystemPrompt));
        }

        var tools = _toolCatalog.GetTools();
        var toolDefinitions = tools.Select(t => t.Definition).ToList();
        var toolsByName = tools.ToDictionary(t => t.Definition.Name);

        for (var round = 0; round < MaxToolCallRounds; round++)
        {
            ChatCompletionResult result;
            try
            {
                result = await _chatClient.CompleteAsync(window.ToChatHistory(), toolDefinitions, cancellationToken);
            }
            catch (ChatCompletionProviderException ex)
            {
                _logger.LogError(ex, "All LLM providers failed while responding to session {SessionId}.", window.SessionId);
                // Do NOT save: the window may already carry earlier rounds' Assistant-with-ToolCalls
                // messages with no matching Tool result, a shape every provider rejects on the next
                // turn. The user's own message is already durable in MySQL regardless of this failure.
                return string.Empty;
            }

            if (result.FinishReason != ChatFinishReason.ToolCalls || result.ToolCalls is not { Count: > 0 })
            {
                window.Add(ChatMessage.Assistant(result.Text ?? string.Empty));
                // Save immediately: this is the normal, successful exit, and it's the only one of the
                // three where the window is guaranteed to end on a complete, well-formed turn.
                await _historyStore.SaveAsync(window, cancellationToken);
                return result.Text ?? string.Empty;
            }

            window.Add(new ChatMessage { Role = ChatRole.Assistant, Content = result.Text, ToolCalls = result.ToolCalls });

            foreach (var toolCall in result.ToolCalls)
            {
                var toolResult = await InvokeToolAsync(toolCall, toolsByName, cancellationToken);
                window.Add(ChatMessage.ToolResult(toolCall.Id, toolCall.Name, toolResult));
            }
        }

        _logger.LogWarning("Session {SessionId} exceeded max tool-call rounds ({MaxRounds}).", window.SessionId, MaxToolCallRounds);
        const string cannedReply = "I wasn't able to complete that request after several tool calls - could you rephrase or simplify it?";
        // CollapseFailedTurn rewinds past up to MaxToolCallRounds of dead tool-calling scaffolding,
        // keeping only the user's own message, before this save - so a chain of failed rounds doesn't
        // occupy the whole 15-slot window going forward.
        window.CollapseFailedTurn(cannedReply);
        await _historyStore.SaveAsync(window, cancellationToken);
        return cannedReply;
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
