using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Data.Entities;

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
    private readonly IConversationalAgent _agent;

    public IntentRouter(
        IntentExtractor intentExtractor,
        CreateBugFlow createBugFlow,
        ViewBugsFlow viewBugsFlow,
        UpdateItemFlow updateItemFlow,
        MyItemsFlow myItemsFlow,
        IConversationalAgent agent)
    {
        _intentExtractor = intentExtractor;
        _createBugFlow = createBugFlow;
        _viewBugsFlow = viewBugsFlow;
        _updateItemFlow = updateItemFlow;
        _myItemsFlow = myItemsFlow;
        _agent = agent;
    }

    public async Task<ChatReply> RouteAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        var extracted = await _intentExtractor.ExtractAsync(userMessage, cancellationToken);

        var flowResult = extracted.Intent switch
        {
            ChatIntent.CreateBug => await _createBugFlow.ExecuteAsync(extracted, cancellationToken),
            ChatIntent.ViewBugs => await _viewBugsFlow.ExecuteAsync(extracted, cancellationToken),
            ChatIntent.UpdateItem => await _updateItemFlow.ExecuteAsync(extracted, cancellationToken),
            ChatIntent.MyItems => await _myItemsFlow.ExecuteAsync(extracted, cancellationToken),
            _ => FlowResult.FallThroughToAgent()
        };

        if (flowResult.Handled)
        {
            return EnsureNonEmpty(flowResult.Output, flowResult.Type, flowResult.WorkItemId, flowResult.TableHeaders, flowResult.TableRows);
        }

        // The free-form conversational agent (AzureBuddyAgent) generates its replies as its own prose -
        // it doesn't know about ChatMessageType at all, so every agent reply is tagged Text. This is a
        // known, real limitation: an agent reply that happens to look like a list or confirmation won't
        // render any richer than plain text client-side. Fixing that would mean either prompting the
        // agent to emit structured output, or having it call the same deterministic-flow builders
        // instead of writing its own prose - both bigger changes than this pass covers.
        var agentReply = await _agent.RespondAsync(sessionId, userMessage, cancellationToken);
        return EnsureNonEmpty(agentReply, ChatMessageType.Text);
    }

    /// <summary>Mirrors the n8n "Ensure Non-Empty Reply" Code node - never let a blank/failed model
    /// response reach the user silently. A blank/failed reply is itself an Error-typed message,
    /// regardless of what type the caller asked for - there's no "empty table" to show.</summary>
    private static ChatReply EnsureNonEmpty(
        string? text,
        ChatMessageType type,
        int? workItemId = null,
        IReadOnlyList<string>? tableHeaders = null,
        IReadOnlyList<IReadOnlyList<string>>? tableRows = null) =>
        string.IsNullOrWhiteSpace(text)
            ? new ChatReply(
                "Sorry, I'm having trouble processing that right now (both the primary and fallback models failed to respond). Please try again in a moment.",
                ChatMessageType.Error)
            : new ChatReply(text.Trim(), type, workItemId, tableHeaders, tableRows);
}
