using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Llm;

/// <summary>
/// Composes an ordered list of providers and tries each in turn, mirroring the n8n agent's
/// needsFallback:true behaviour (Gemini primary, Ollama fallback). The first provider whose
/// call succeeds wins; ChatCompletionProviderException from one provider moves on to the next.
/// If every provider fails, the last exception is rethrown.
/// </summary>
public sealed class FallbackChatClient : IChatCompletionClient
{
    private readonly IReadOnlyList<IChatCompletionClient> _providersInOrder;
    private readonly ILogger<FallbackChatClient> _logger;

    public string ProviderName => "Fallback(" + string.Join(",", _providersInOrder.Select(p => p.ProviderName)) + ")";

    public FallbackChatClient(IReadOnlyList<IChatCompletionClient> providersInOrder, ILogger<FallbackChatClient> logger)
    {
        if (providersInOrder.Count == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providersInOrder));
        }

        _providersInOrder = providersInOrder;
        _logger = logger;
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        ChatHistory history,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        Exception? lastFailure = null;

        foreach (var provider in _providersInOrder)
        {
            try
            {
                return await provider.CompleteAsync(history, tools, cancellationToken);
            }
            catch (ChatCompletionProviderException ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} failed, trying next fallback provider.", provider.ProviderName);
                lastFailure = ex;
            }
        }

        throw new ChatCompletionProviderException(
            ProviderName,
            "All configured LLM providers failed.",
            lastFailure);
    }
}
