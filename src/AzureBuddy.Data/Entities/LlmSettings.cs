namespace AzureBuddy.Data.Entities;

/// <summary>
/// The app-wide LLM provider configuration - which providers are enabled and in what fallback order,
/// plus each provider's connection details. Unlike UserAdoSettings (one row per user), this is a
/// SINGLETON row: there is exactly one LLM configuration for the whole app, editable only by an Admin
/// (see LlmSettingsController), because a model/API key choice is an infrastructure decision, not a
/// per-user preference. Enforced by always querying/writing the row with Id == SingletonId, never by a
/// unique constraint on some other column.
///
/// This table is optional in the literal sense: if no row exists yet, the app runs on whatever
/// appsettings.json's "Llm" section says (see LlmSettingsProvider) - saving from the admin screen for
/// the first time is what creates this row and takes over from the config file.
///
/// GeminiEncryptedApiKey mirrors UserAdoSettings.EncryptedPat exactly: ciphertext produced by
/// LlmApiKeyProtector (Data Protection API), decrypted only in memory, immediately before an outbound
/// call to Gemini.
/// </summary>
public sealed class LlmSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Ordered provider names, comma-separated (e.g. "Gemini,Ollama") - the first is primary,
    /// each subsequent one is a fallback for the ones before it. Stored as a single delimited string
    /// rather than a related table since it's always read/written as one small ordered list, never
    /// queried by individual provider name.</summary>
    public required string ProvidersCsv { get; set; }

    public string GeminiModel { get; set; } = "gemini-1.5-flash";
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public int GeminiTimeoutSeconds { get; set; } = 30;
    /// <summary>Null until an admin has ever saved a Gemini key through this screen - distinguished
    /// from "" so LlmSettingsService can tell "never configured" apart from "cleared".</summary>
    public string? GeminiEncryptedApiKey { get; set; }

    public string OllamaModel { get; set; } = "qwen2.5:14b-instruct";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public int OllamaNumCtx { get; set; } = 8192;
    public int OllamaTimeoutSeconds { get; set; } = 60;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
