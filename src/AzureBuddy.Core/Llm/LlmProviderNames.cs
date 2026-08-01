namespace AzureBuddy.Core.Llm;

/// <summary>Single source of truth for provider name strings, used both as the DI keyed-service key
/// and as each client's ProviderName - previously these were independently hardcoded in 3 places
/// (GeminiChatClient, OllamaChatClient, and the old switch in LlmServiceCollectionExtensions) with
/// nothing enforcing they stayed in sync.</summary>
public static class LlmProviderNames
{
    public const string Gemini = "Gemini";
    public const string Ollama = "Ollama";
}
