using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Llm;

/// <summary>
/// A single LLM provider's chat-completion capability, abstracted away from that provider's wire format.
/// Implement this once per provider (Gemini, Ollama, and anything added later) - nothing else in the app
/// should depend on a provider-specific SDK or DTO.
/// </summary>
public interface IChatCompletionClient
{
    /// <summary>Stable identifier for this provider, e.g. "Gemini", "Ollama". Used in config and logging.</summary>
    string ProviderName { get; }

    Task<ChatCompletionResult> CompleteAsync(
        ChatHistory history,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown by a provider implementation for any failure that should be treated as transient/retryable
/// by a fallback chain (timeout, 5xx, malformed response, rate limit). Providers should let non-transient
/// failures (e.g. bad request due to a bug) surface as their natural exception type instead.</summary>
public sealed class ChatCompletionProviderException : Exception
{
    public string ProviderName { get; }

    public ChatCompletionProviderException(string providerName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
    }
}
