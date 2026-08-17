using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Data.Entities;
// AzureBuddy.Data.Entities (for ChatMessageRole/ChatMessageType below) and Llm.Models both declare a
// ChatMessage type - this alias is what RouteAsync uses to build entries for the agent's own
// in-memory ChatHistory, not the persisted database entity.
using LlmChatMessage = AzureBuddy.Core.Llm.Models.ChatMessage;

namespace AzureBuddy.Core.Routing;

/// <summary>
/// Ports the n8n "Route Intent" Switch node: dispatches on the extracted intent to the matching
/// deterministic flow, and falls through to the full conversational agent whenever the intent is
/// "other" or the deterministic flow couldn't resolve the request by itself.
/// </summary>
public sealed class IntentRouter
{
    private readonly IntentExtractor _intentExtractor;
    private readonly CreateBugFlow _createBugFlow;
    private readonly ViewBugsFlow _viewBugsFlow;
    private readonly UpdateItemFlow _updateItemFlow;
    private readonly MyItemsFlow _myItemsFlow;
    private readonly GetPrioritizedWorkItemsFlow _getPrioritizedWorkItemsFlow;
    private readonly IConversationalAgent _agent;
    private readonly IChatHistoryStore _historyStore;

    public IntentRouter(
        IntentExtractor intentExtractor,
        CreateBugFlow createBugFlow,
        ViewBugsFlow viewBugsFlow,
        UpdateItemFlow updateItemFlow,
        MyItemsFlow myItemsFlow,
        GetPrioritizedWorkItemsFlow getPrioritizedWorkItemsFlow,
        IConversationalAgent agent,
        IChatHistoryStore historyStore)
    {
        _intentExtractor = intentExtractor;
        _createBugFlow = createBugFlow;
        _viewBugsFlow = viewBugsFlow;
        _updateItemFlow = updateItemFlow;
        _myItemsFlow = myItemsFlow;
        _getPrioritizedWorkItemsFlow = getPrioritizedWorkItemsFlow;
        _agent = agent;
        _historyStore = historyStore;
    }

    public async Task<ChatReply> RouteAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        var extracted = await _intentExtractor.ExtractAsync(userMessage, cancellationToken);

        // Loaded exactly once per turn, here - above the fork between the deterministic-flow path and
        // the agent path below - because IntentRouter is the only place that sits above both. AddUserTurn
        // (not a plain Add) guards against the window already ending with this exact message: ChatController.
        // PostAsync persists the user's message to MySQL BEFORE calling RouteAsync, so on a cache miss
        // DbChatHistorySource can rehydrate a window whose last row already IS this message.
        var window = await _historyStore.LoadAsync(sessionId, cancellationToken);
        window.AddUserTurn(LlmChatMessage.User(userMessage));

        // Every deterministic flow below can only act on the single request `extracted.Intent`
        // captures. A message that asks for more than one thing ("file a bug for X and show my open
        // items") would otherwise silently answer one half and drop the other - so route those
        // straight to the full conversational agent, which reasons over the whole raw message and can
        // make multiple tool calls in one turn.
        var flowResult = extracted.HasAdditionalRequest
            ? FlowResult.FallThroughToAgent()
            : extracted.Intent switch
            {
                ChatIntent.CreateBug => await _createBugFlow.ExecuteAsync(extracted, cancellationToken),
                ChatIntent.ViewBugs => await _viewBugsFlow.ExecuteAsync(extracted, cancellationToken),
                ChatIntent.UpdateItem => await _updateItemFlow.ExecuteAsync(extracted, cancellationToken),
                ChatIntent.MyItems => await _myItemsFlow.ExecuteAsync(extracted, cancellationToken),
                ChatIntent.PrioritizeWorkItems => await _getPrioritizedWorkItemsFlow.ExecuteAsync(extracted, cancellationToken),
                _ => FlowResult.FallThroughToAgent()
            };

        if (flowResult.Handled)
        {
            var reply = EnsureNonEmpty(flowResult.Output, flowResult.Type, flowResult.WorkItemId, flowResult.TableHeaders, flowResult.TableRows);

            // A deterministic flow just answered this turn without ever going through
            // AzureBuddyAgent - but a LATER turn ("summarize these", "what about #12352 specifically")
            // might fall through to the agent and need to know what was just shown. Recording the turn
            // into the same per-session window the agent reads from (keyed by this same sessionId - see
            // ChatController's comment on that) keeps the agent's memory a complete record of the
            // conversation, not just the turns it happened to handle itself. The reply text already has
            // the rendered markdown table baked in (see MyItemsFlow/ViewBugsFlow), so the agent can
            // literally read the ids/titles/states straight out of its own history.
            window.Add(LlmChatMessage.Assistant(reply.Text));
            await _historyStore.SaveAsync(window, cancellationToken);

            return reply;
        }

        // The free-form conversational agent (AzureBuddyAgent) generates its replies as its own prose -
        // it doesn't know about ChatMessageType at all, so every agent reply is tagged Text. This is a
        // known, real limitation: an agent reply that happens to look like a list or confirmation won't
        // render any richer than plain text client-side. Fixing that would mean either prompting the
        // agent to emit structured output, or having it call the same deterministic-flow builders
        // instead of writing its own prose - both bigger changes than this pass covers.
        //
        // The agent, not this router, decides whether/how to save `window` from here - see
        // AzureBuddyAgent.RespondAsync's doc comment for why only it knows which exit is safe to persist.
        var agentReply = await _agent.RespondAsync(window, cancellationToken);
        return EnsureNonEmpty(agentReply, ChatMessageType.Text);
    }

    /// <summary>Mirrors the n8n "Ensure Non-Empty Reply" Code node - never let a blank/failed model
    /// response reach the user silently. A blank/failed reply is itself an Error-typed message,
    /// regardless of what type the caller asked for - there's no "empty table" to show.
    ///
    /// This is a deliberate choice, not an oversight: AzureBuddyAgent.RespondAsync catches
    /// ChatCompletionProviderException itself and returns string.Empty (logging at Error level, which is
    /// what should feed alerting), and IntentExtractor.ExtractAsync does the same for the classification
    /// call, falling back to ChatIntent.Other. Both mean GlobalExceptionHandler's 502 "llm_provider_unavailable"
    /// mapping is effectively unreachable from a normal conversational turn - a provider outage always
    /// surfaces as this in-chat Error message on an ordinary 200, never as a 502. That trade favors chat
    /// UX (never show the user a raw HTTP error) over an externally-observable outage signal; watch the
    /// Error-level agent/extractor logs, not response status codes, to detect provider outages.</summary>
    private static ChatReply EnsureNonEmpty(
        string? text,
        ChatMessageType type,
        int? workItemId = null,
        IReadOnlyList<string>? tableHeaders = null,
        IReadOnlyList<IReadOnlyList<string>>? tableRows = null) =>
        string.IsNullOrWhiteSpace(text)
            ? new ChatReply(
                // Deliberately doesn't say "both the primary and fallback models" - how many providers
                // are configured is an admin setting (often just one), so naming two invents detail the
                // reader can act on wrongly, sending them to check a fallback that doesn't exist.
                "Sorry, I couldn't get a response from the AI model just now. It may be slow to respond or unreachable - please try again in a moment, or check the model settings if this keeps happening.",
                ChatMessageType.Error)
            : new ChatReply(text.Trim(), type, workItemId, tableHeaders, tableRows);
}
