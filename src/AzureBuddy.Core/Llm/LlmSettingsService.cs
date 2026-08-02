using System.Security.Cryptography;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Llm;

/// <summary>
/// Reads/writes the one, app-wide LLM configuration row. Unlike UserAdoConfigService there's no
/// userId parameter anywhere here - every method operates on the single LlmSettings.SingletonId row,
/// because this is infrastructure configuration, not a per-user preference (see LlmSettings.cs).
///
/// The one thing this class does that UserAdoConfigService doesn't: SaveAsync ends by calling
/// ILlmSettingsProvider.Refresh(...), which is what makes a saved change take effect on the very next
/// chat request instead of requiring an app restart. Every other write in this codebase (ADO settings,
/// chat messages) only ever needs to persist to the database - the database IS the source of truth
/// the next request reads from. LLM settings are different because GeminiChatClient/OllamaChatClient
/// don't read the database directly (a DB round trip on every single chat turn would be wasteful) -
/// they read the in-memory ILlmSettingsProvider, so this service has to keep both in sync explicitly.
/// </summary>
public sealed class LlmSettingsService
{
    private readonly AppDbContext _dbContext;
    private readonly LlmApiKeyProtector _apiKeyProtector;
    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILogger<LlmSettingsService> _logger;

    public LlmSettingsService(
        AppDbContext dbContext,
        LlmApiKeyProtector apiKeyProtector,
        ILlmSettingsProvider settingsProvider,
        ILogger<LlmSettingsService> logger)
    {
        _dbContext = dbContext;
        _apiKeyProtector = apiKeyProtector;
        _settingsProvider = settingsProvider;
        _logger = logger;
    }

    /// <summary>Called once at app startup (see Program.cs) to make an already-saved database row
    /// take effect immediately, instead of the app running on appsettings.json's Llm section until
    /// the next admin save. A pure read-and-refresh - unlike SaveAsync, this never writes to the
    /// database, so restarting the app doesn't have the side effect of bumping UpdatedAt on a row
    /// nothing about actually changed.</summary>
    public async Task LoadFromDatabaseIfPresentAsync(CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(cancellationToken);
        if (row is not null)
        {
            _settingsProvider.Refresh(BuildEffectiveOptions(row));
        }
    }

    public async Task<LlmSettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(cancellationToken);

        // No row saved yet - the app is still running on appsettings.json's Llm section (see
        // LlmSettingsProvider's constructor). Show the admin what's ACTUALLY in effect right now
        // (ILlmSettingsProvider.Current) rather than an empty form, so the first thing they see
        // before ever saving is the truth, not a blank slate that misrepresents live behavior.
        if (row is null)
        {
            var current = _settingsProvider.Current;
            var primary = current.Providers.FirstOrDefault() ?? LlmProviderNames.Gemini;
            return new LlmSettingsView(
                IsStoredInDatabase: false,
                PrimaryProvider: primary,
                UseFallback: current.Providers.Count > 1,
                current.Gemini.Model, current.Gemini.BaseUrl, current.Gemini.TimeoutSeconds,
                MaskedGeminiApiKey: MaskKey(current.Gemini.ApiKey),
                current.Ollama.Model, current.Ollama.BaseUrl, current.Ollama.NumCtx, current.Ollama.TimeoutSeconds,
                UpdatedAt: null);
        }

        return ToView(row);
    }

    public async Task<LlmSettingsView> SaveAsync(SaveLlmSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var primary = NormalizeProviderName(request.PrimaryProvider);
        var other = primary == LlmProviderNames.Gemini ? LlmProviderNames.Ollama : LlmProviderNames.Gemini;
        var providers = request.UseFallback ? new[] { primary, other } : new[] { primary };

        var row = await FindAsync(cancellationToken);
        if (row is null)
        {
            row = new LlmSettings { ProvidersCsv = string.Join(',', providers) };
            _dbContext.LlmSettings.Add(row);
        }

        row.ProvidersCsv = string.Join(',', providers);
        row.GeminiModel = request.GeminiModel;
        row.GeminiBaseUrl = request.GeminiBaseUrl;
        row.GeminiTimeoutSeconds = request.GeminiTimeoutSeconds;
        // A blank submitted key means "leave it alone" - only overwrite the stored ciphertext when
        // the admin actually typed something. See SaveLlmSettingsRequest's comment for why this
        // differs from the ADO PAT screen's always-required behavior.
        if (!string.IsNullOrWhiteSpace(request.GeminiApiKey))
        {
            row.GeminiEncryptedApiKey = _apiKeyProtector.Encrypt(request.GeminiApiKey);
        }
        row.OllamaModel = request.OllamaModel;
        row.OllamaBaseUrl = request.OllamaBaseUrl;
        row.OllamaNumCtx = request.OllamaNumCtx;
        row.OllamaTimeoutSeconds = request.OllamaTimeoutSeconds;
        row.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // The save that actually matters for behavior: every chat request from now on builds its
        // provider chain from this new value instead of whatever was in effect before.
        _settingsProvider.Refresh(BuildEffectiveOptions(row));
        _logger.LogInformation("LLM settings updated - providers now: {Providers}", row.ProvidersCsv);

        return ToView(row);
    }

    private Task<LlmSettings?> FindAsync(CancellationToken cancellationToken) =>
        _dbContext.LlmSettings.SingleOrDefaultAsync(s => s.Id == LlmSettings.SingletonId, cancellationToken);

    private static string NormalizeProviderName(string requested)
    {
        // Validated against the actual known set rather than trusting client input outright - an
        // unrecognized provider name here would otherwise reach GetRequiredKeyedService in
        // LlmServiceCollectionExtensions and blow up as an unhandled DI resolution failure on the
        // NEXT chat request, not as a clean 400 at save time.
        if (string.Equals(requested, LlmProviderNames.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return LlmProviderNames.Gemini;
        }
        if (string.Equals(requested, LlmProviderNames.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            return LlmProviderNames.Ollama;
        }
        throw new ArgumentException($"Unknown provider '{requested}' - expected '{LlmProviderNames.Gemini}' or '{LlmProviderNames.Ollama}'.");
    }

    private LlmOptions BuildEffectiveOptions(LlmSettings row) => new()
    {
        Providers = row.ProvidersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Gemini = new GeminiOptions
        {
            // Decrypted here, in memory, and handed straight to ILlmSettingsProvider - never logged,
            // never part of any HTTP response. GeminiChatClient reads it back out the same way any
            // config value would be read; there is no second copy of the plaintext anywhere.
            ApiKey = row.GeminiEncryptedApiKey is null ? string.Empty : _apiKeyProtector.Decrypt(row.GeminiEncryptedApiKey),
            Model = row.GeminiModel,
            BaseUrl = row.GeminiBaseUrl,
            TimeoutSeconds = row.GeminiTimeoutSeconds,
        },
        Ollama = new OllamaOptions
        {
            BaseUrl = row.OllamaBaseUrl,
            Model = row.OllamaModel,
            NumCtx = row.OllamaNumCtx,
            TimeoutSeconds = row.OllamaTimeoutSeconds,
        },
    };

    private LlmSettingsView ToView(LlmSettings row)
    {
        var providers = row.ProvidersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var maskedKey = row.GeminiEncryptedApiKey is null ? null : MaskKey(DecryptSafely(row.GeminiEncryptedApiKey));

        return new LlmSettingsView(
            IsStoredInDatabase: true,
            PrimaryProvider: providers.FirstOrDefault() ?? LlmProviderNames.Gemini,
            UseFallback: providers.Length > 1,
            row.GeminiModel, row.GeminiBaseUrl, row.GeminiTimeoutSeconds, maskedKey,
            row.OllamaModel, row.OllamaBaseUrl, row.OllamaNumCtx, row.OllamaTimeoutSeconds,
            row.UpdatedAt);
    }

    /// <summary>Same masking convention as UserAdoConfigService.MaskPat: last 4 characters visible,
    /// the rest replaced with bullets. Decrypting only to immediately re-mask is safe here for the
    /// same reason it's safe there - this is the credential's own owner (an admin) reading it back,
    /// and the full plaintext still never leaves this method.</summary>
    private static string MaskKey(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }
        var lastFour = plaintext.Length >= 4 ? plaintext[^4..] : plaintext;
        return new string('•', 8) + lastFour;
    }

    private string DecryptSafely(string encrypted)
    {
        try
        {
            return _apiKeyProtector.Decrypt(encrypted);
        }
        catch (CryptographicException ex)
        {
            // Same fallback as UserAdoConfigService.MaskPat - if the Data Protection key ring ever
            // rotated or was lost, a stored key becomes unreadable. Surfacing that as "the mask looks
            // odd" on a GET is far better than throwing a 500 on what's otherwise a simple read.
            _logger.LogWarning(ex, "Could not decrypt stored Gemini API key - key ring may have rotated.");
            return string.Empty;
        }
    }
}
