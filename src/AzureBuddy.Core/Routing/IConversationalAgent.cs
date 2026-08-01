namespace AzureBuddy.Core.Routing;

/// <summary>
/// The full conversational fallback - handles "other" intents and anything the deterministic flows
/// couldn't resolve on their own. Implemented by AzureBuddyAgent (Agent module), kept as an interface
/// here so IntentRouter doesn't depend on the agent's LLM/tool-calling internals.
/// </summary>
public interface IConversationalAgent
{
    Task<string> RespondAsync(string sessionId, string userMessage, CancellationToken cancellationToken = default);
}
