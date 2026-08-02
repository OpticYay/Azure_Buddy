namespace AzureBuddy.Core.Llm;

/// <summary>Binds to the "Llm" config section. "Providers" lists provider names in fallback order,
/// e.g. ["Gemini", "Ollama"] - primary first, each subsequent entry is a fallback for the ones before it.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public List<string> Providers { get; init; } = new();
    public GeminiOptions Gemini { get; init; } = new();
    public OllamaOptions Ollama { get; init; } = new();
}

public sealed class GeminiOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gemini-1.5-flash";
    public string BaseUrl { get; init; } = "https://generativelanguage.googleapis.com/v1beta";
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class OllamaOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen2.5:14b-instruct";
    public int NumCtx { get; init; } = 8192;
    public int TimeoutSeconds { get; init; } = 60;
}
