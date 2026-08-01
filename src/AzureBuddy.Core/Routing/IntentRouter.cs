using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;

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

    public async Task<string> RouteAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default)
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
            return EnsureNonEmpty(flowResult.Output);
        }

        var agentReply = await _agent.RespondAsync(sessionId, userMessage, cancellationToken);
        return EnsureNonEmpty(agentReply);
    }

    /// <summary>Mirrors the n8n "Ensure Non-Empty Reply" Code node - never let a blank/failed model
    /// response reach the user silently.</summary>
    private static string EnsureNonEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? "Sorry, I'm having trouble processing that right now (both the primary and fallback models failed to respond). Please try again in a moment."
            : text.Trim();
}
