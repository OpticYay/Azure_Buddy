namespace AzureBuddy.Core.Llm;

/// <summary>
/// The live, in-memory source of truth for LLM provider configuration - what GeminiChatClient,
/// OllamaChatClient, and the fallback chain builder actually read, instead of reading IOptions
/// &lt;LlmOptions&gt; (a fixed snapshot bound once from appsettings.json at container-build time) directly.
///
/// Why this exists instead of just re-binding IOptions: appsettings.json's Llm section can't be edited
/// through this app's own admin screen (files on disk, not a database row), and even IOptionsMonitor's
/// "reload on file change" only helps for the config FILE, not for LlmSettingsController's PUT.
/// Threading a database-backed value through cleanly meant introducing one seam - this interface -
/// that starts out backed by appsettings.json's values and gets overwritten in memory the moment an
/// admin saves a change (see LlmSettingsService.SaveAsync calling Refresh), with no app restart and no
/// polling involved.
/// </summary>
public interface ILlmSettingsProvider
{
    /// <summary>The provider configuration in effect right now. Every chat completion started after a
    /// Refresh() call sees the new value; anything already in flight keeps running against whatever it
    /// already read - there's no mid-request config swap to reason about.</summary>
    LlmOptions Current { get; }

    /// <summary>Called by LlmSettingsService immediately after a successful admin save. Not intended
    /// to be called from anywhere else.</summary>
    void Refresh(LlmOptions updated);
}

/// <summary>Plain in-memory holder - a single reference-type field swap, which is atomic in .NET
/// (readers always see either the old or the new LlmOptions instance in full, never a half-written
/// one), so no explicit locking is needed for this specific "replace the whole value" access pattern.</summary>
public sealed class LlmSettingsProvider : ILlmSettingsProvider
{
    private LlmOptions _current;

    /// <summary>Seeded from appsettings.json's bound LlmOptions at construction - this is what makes
    /// the app work unmodified on a fresh deployment where no admin has saved anything to the database
    /// yet. Program.cs overwrites this with the database row's values shortly after startup, if one
    /// exists (see its "LLM settings: load from database" block).</summary>
    public LlmSettingsProvider(Microsoft.Extensions.Options.IOptions<LlmOptions> appSettingsDefaults)
    {
        _current = appSettingsDefaults.Value;
    }

    public LlmOptions Current => _current;

    public void Refresh(LlmOptions updated) => _current = updated;
}
