using System.ComponentModel.DataAnnotations;

namespace AzureBuddy.Core.Llm;

/// <summary>
/// What an admin submits to PUT /api/admin/llm. GeminiApiKey is nullable and, unlike
/// SaveAdoSettingsRequest.PersonalAccessToken, deliberately NOT [Required]: leaving it blank means
/// "keep whatever key is already saved" (see LlmSettingsService.SaveAsync). That's a real difference
/// from the ADO PAT screen, not an inconsistency - ADO PATs are edited by every regular user of the
/// app, so forcing re-entry every time is a small, acceptable friction spread across many people. This
/// key is edited only by admins, rarely, and forcing a full re-paste of a long API key on every minor
/// edit (say, just bumping a timeout) would be disproportionate friction concentrated on a few people.
/// PrimaryProvider/UseFallback (not a raw ordered list) because there are exactly two providers today -
/// a full drag-to-reorder list is speculative generality for a choice that's really just "which one
/// first, and should the other be a backup."
/// </summary>
public sealed record SaveLlmSettingsRequest(
    [Required] string PrimaryProvider,
    bool UseFallback,
    [Required] string GeminiModel,
    [Required, Url] string GeminiBaseUrl,
    [Range(1, 600)] int GeminiTimeoutSeconds,
    string? GeminiApiKey,
    [Required] string OllamaModel,
    [Required, Url] string OllamaBaseUrl,
    [Range(256, 131072)] int OllamaNumCtx,
    [Range(1, 600)] int OllamaTimeoutSeconds);

/// <summary>What GET /api/admin/llm returns. MaskedGeminiApiKey follows the exact same convention as
/// AdoSettingsView.MaskedPat - the real key is never sent to the browser, only a "••••••••1234"-style
/// display value (or null if no key has ever been saved).</summary>
public sealed record LlmSettingsView(
    bool IsStoredInDatabase,
    string PrimaryProvider,
    bool UseFallback,
    string GeminiModel,
    string GeminiBaseUrl,
    int GeminiTimeoutSeconds,
    string? MaskedGeminiApiKey,
    string OllamaModel,
    string OllamaBaseUrl,
    int OllamaNumCtx,
    int OllamaTimeoutSeconds,
    DateTime? UpdatedAt);
