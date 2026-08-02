using AzureBuddy.Core.Llm.Providers.Gemini;
using AzureBuddy.Core.Llm.Providers.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBuddy.Core.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers each concrete provider as a keyed service (key = its LlmProviderNames constant), and
    /// registers IChatCompletionClient as a FallbackChatClient that resolves the CURRENT (not
    /// startup-time) Llm:Providers list, in order, via GetRequiredKeyedService. Keyed DI (instead of
    /// the old switch-on-string) means adding a new provider is pure addition - implement
    /// IChatCompletionClient and add its two registration lines below - rather than editing an
    /// existing branch of control flow (Open/Closed Principle: the fallback-chain-building logic
    /// itself never needs to change for a new provider).
    ///
    /// IChatCompletionClient is registered Scoped, not Singleton, and that change is deliberate and
    /// load-bearing: a Singleton factory only ever runs ONCE, the first time anything asks for an
    /// IChatCompletionClient, and every request after that - forever - would get back the exact same
    /// FallbackChatClient built from whatever LlmOptions existed at that first resolution. That would
    /// silently defeat ILlmSettingsProvider entirely: an admin's saved change would sit in the
    /// database and in the provider's Current property, completely correctly, while every actual chat
    /// request kept using the stale chain built at startup. Scoped (one instance per HTTP request)
    /// means this factory - and therefore ILlmSettingsProvider.Current - is read fresh on every request.
    /// </summary>
    public static IServiceCollection AddLlmProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        // Singleton: one shared in-memory "current settings" value for the whole app, seeded from
        // appsettings.json at construction and overwritten by Program.cs's startup DB load and by
        // every subsequent admin save (LlmSettingsService.SaveAsync) - see ILlmSettingsProvider.cs.
        services.AddSingleton<ILlmSettingsProvider, LlmSettingsProvider>();
        services.AddScoped<LlmApiKeyProtector>();
        services.AddScoped<LlmSettingsService>();

        services.AddHttpClient<GeminiChatClient>();
        services.AddHttpClient<OllamaChatClient>();

        services.AddKeyedTransient<IChatCompletionClient>(
            LlmProviderNames.Gemini, (sp, _) => sp.GetRequiredService<GeminiChatClient>());
        services.AddKeyedTransient<IChatCompletionClient>(
            LlmProviderNames.Ollama, (sp, _) => sp.GetRequiredService<OllamaChatClient>());

        services.AddScoped<IChatCompletionClient>(sp =>
        {
            var options = sp.GetRequiredService<ILlmSettingsProvider>().Current;

            if (options.Providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "No LLM provider is configured - set it from the admin LLM settings screen, or Llm:Providers in appsettings.json.");
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
