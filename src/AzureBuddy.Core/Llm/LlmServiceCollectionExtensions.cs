using AzureBuddy.Core.Llm.Providers.Gemini;
using AzureBuddy.Core.Llm.Providers.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers each concrete provider named in Llm:Providers plus a keyed lookup, and registers
    /// IChatCompletionClient as a FallbackChatClient over them in the configured order.
    ///
    /// Adding a new provider later: implement IChatCompletionClient, register it in the switch below
    /// under its own name, and add that name to appsettings' Llm:Providers list.
    /// </summary>
    public static IServiceCollection AddLlmProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        services.AddHttpClient<GeminiChatClient>();
        services.AddHttpClient<OllamaChatClient>();

        services.AddSingleton<IChatCompletionClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value;

            if (options.Providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "Llm:Providers must list at least one provider name (e.g. [\"Gemini\", \"Ollama\"]).");
            }

            var chain = options.Providers.Select(name => ResolveProvider(sp, name)).ToList();

            return new FallbackChatClient(
                chain,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FallbackChatClient>>());
        });

        return services;
    }

    private static IChatCompletionClient ResolveProvider(IServiceProvider sp, string providerName) => providerName switch
    {
        "Gemini" => sp.GetRequiredService<GeminiChatClient>(),
        "Ollama" => sp.GetRequiredService<OllamaChatClient>(),
        _ => throw new InvalidOperationException(
            $"Unknown LLM provider '{providerName}' in Llm:Providers. " +
            "Register it in LlmServiceCollectionExtensions.ResolveProvider.")
    };
}
