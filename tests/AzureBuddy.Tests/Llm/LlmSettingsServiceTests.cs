using AzureBuddy.Core.Llm;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBuddy.Tests.Llm;

/// <summary>Covers LlmSettingsService.BuildEffectiveOptions's hardening: a stored Gemini API key that
/// fails to decrypt (Data Protection key ring rotated/lost) must not throw - this method runs at app
/// startup (LoadFromDatabaseIfPresentAsync) where an unhandled exception would crash the whole app, not
/// just one request.</summary>
public class LlmSettingsServiceTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // Two entirely separate ephemeral Data Protection key rings - a key encrypted with one can never be
    // decrypted with the other, the same "key ring rotated/was lost" scenario the Redis cutover could
    // hit mid-migration if DataProtectionKeyImporter hasn't run yet.
    private static LlmApiKeyProtector NewProtector()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"azurebuddy-test-keys-{Guid.NewGuid():N}");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyPath));
        return new LlmApiKeyProtector(provider);
    }

    private static LlmSettingsProvider NewSettingsProvider() =>
        new(Options.Create(new LlmOptions()));

    [Fact]
    public async Task LoadFromDatabaseIfPresentAsync_KeyEncryptedWithADifferentKeyRing_DoesNotThrowAndFallsBackToEmptyApiKey()
    {
        await using var dbContext = NewDbContext();
        var writerProtector = NewProtector();
        var readerProtector = NewProtector();

        dbContext.LlmSettings.Add(new LlmSettings
        {
            ProvidersCsv = "Gemini",
            GeminiEncryptedApiKey = writerProtector.Encrypt("real-api-key"),
        });
        await dbContext.SaveChangesAsync();

        var settingsProvider = NewSettingsProvider();
        var service = new LlmSettingsService(dbContext, readerProtector, settingsProvider, new NoOpLlmSettingsChangePublisher(), NullLogger<LlmSettingsService>.Instance);

        await service.LoadFromDatabaseIfPresentAsync();

        Assert.Equal(string.Empty, settingsProvider.Current.Gemini.ApiKey);
    }

    [Fact]
    public async Task GetAsync_KeyEncryptedWithADifferentKeyRing_ReturnsEmptyMaskInsteadOfThrowing()
    {
        await using var dbContext = NewDbContext();
        var writerProtector = NewProtector();
        var readerProtector = NewProtector();

        dbContext.LlmSettings.Add(new LlmSettings
        {
            ProvidersCsv = "Gemini",
            GeminiEncryptedApiKey = writerProtector.Encrypt("real-api-key"),
        });
        await dbContext.SaveChangesAsync();

        var service = new LlmSettingsService(dbContext, readerProtector, NewSettingsProvider(), new NoOpLlmSettingsChangePublisher(), NullLogger<LlmSettingsService>.Instance);

        var view = await service.GetAsync();

        Assert.Equal(string.Empty, view.MaskedGeminiApiKey);
    }
}
