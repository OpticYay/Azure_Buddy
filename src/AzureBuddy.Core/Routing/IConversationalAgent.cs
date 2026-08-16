using AzureBuddy.Core.Agent;

namespace AzureBuddy.Core.Routing;

/// <summary>
/// The full conversational fallback - handles "other" intents and anything the deterministic flows
/// couldn't resolve on their own. Implemented by AzureBuddyAgent (Agent module), kept as an interface
/// here so IntentRouter doesn't depend on the agent's LLM/tool-calling internals.
///
/// Takes the already-loaded ChatSessionWindow (IntentRouter loads it once, above the fork between the
/// deterministic-flow path and this one) rather than a bare sessionId, and returns just the reply text -
/// the agent, not the router, decides whether/how to save the window, since only it knows which of its
/// three exit paths actually produced something worth persisting (see AzureBuddyAgent.RespondAsync).
/// </summary>
public interface IConversationalAgent
{
    Task<string> RespondAsync(ChatSessionWindow window, CancellationToken cancellationToken = default);
}
