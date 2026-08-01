using AzureBuddy.Core.Llm.Providers.Gemini;
using AzureBuddy.Core.Llm.Providers.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers each concrete provider as a keyed service (key = its LlmProviderNames constant), and
    /// registers IChatCompletionClient as a FallbackChatClient that resolves the configured Llm:Providers
    /// list, in order, via GetRequiredKeyedService. Keyed DI (instead of the old switch-on-string) means
    /// adding a new provider is pure addition - implement IChatCompletionClient and add its two
    /// registration lines below - rather than editing an existing branch of control flow (Open/Closed
    /// Principle: the fallback-chain-building logic itself never needs to change for a new provider).
    /// </summary>
    public static IServiceCollection AddLlmProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        services.AddHttpClient<GeminiChatClient>();
        services.AddHttpClient<OllamaChatClient>();

        services.AddKeyedTransient<IChatCompletionClient>(
            LlmProviderNames.Gemini, (sp, _) => sp.GetRequiredService<GeminiChatClient>());
        services.AddKeyedTransient<IChatCompletionClient>(
            LlmProviderNames.Ollama, (sp, _) => sp.GetRequiredService<OllamaChatClient>());

        services.AddSingleton<IChatCompletionClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value;

            if (options.Providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "Llm:Providers must list at least one provider name (e.g. [\"Gemini\", \"Ollama\"]).");
            }

            var chain = options.Providers
                .Select(name => sp.GetRequiredKeyedService<IChatCompletionClient>(name))
                .ToList();

            return new FallbackChatClient(
                chain,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FallbackChatClient>>());
        });

        return services;
    }
}
