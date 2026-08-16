using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Settings;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using AzureBuddy.Tests.Integration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests.Settings;

/// <summary>Covers UserAdoConfigService.GetConnectionContextAsync's hardening: a stored PAT that fails
/// to decrypt (e.g. the Data Protection key ring rotated or was lost - exactly what could happen mid
/// cutover to Redis-backed keys, see DataProtectionKeyImporter) must surface as the same
/// AdoNotConfiguredException ChatController already has a handler for, not an unhandled
/// CryptographicException that would 500 a live chat turn.</summary>
public class UserAdoConfigServiceTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // Two entirely separate ephemeral Data Protection key rings, each in its own temp directory - a PAT
    // encrypted with one can never be decrypted with the other, which is exactly what "the key ring
    // rotated/was lost" looks like in practice, without needing to fake IDataProtector directly.
    private static PatProtector NewPatProtector()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"azurebuddy-test-keys-{Guid.NewGuid():N}");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyPath));
        return new PatProtector(provider);
    }

    [Fact]
    public async Task GetConnectionContextAsync_PatEncryptedWithADifferentKeyRing_ThrowsAdoNotConfiguredExceptionWithClearMessage()
    {
        await using var dbContext = NewDbContext();
        var writerProtector = NewPatProtector();
        var readerProtector = NewPatProtector();
        const string userId = "user-1";

        dbContext.UserAdoSettings.Add(new UserAdoSettings
        {
            UserId = userId,
            OrganizationUrl = "https://dev.azure.com/org",
            DefaultProject = "Proj",
            EncryptedPat = writerProtector.Encrypt("real-pat-value"),
        });
        await dbContext.SaveChangesAsync();

        var service = new UserAdoConfigService(dbContext, readerProtector, new FakeAdoClient(), NullLogger<UserAdoConfigService>.Instance);

        var ex = await Assert.ThrowsAsync<AdoNotConfiguredException>(() => service.GetConnectionContextAsync(userId));
        Assert.Contains("re-enter your PAT", ex.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_PatEncryptedWithADifferentKeyRing_ReturnsGracefulFailureNotAnException()
    {
        await using var dbContext = NewDbContext();
        var writerProtector = NewPatProtector();
        var readerProtector = NewPatProtector();
        const string userId = "user-1";

        dbContext.UserAdoSettings.Add(new UserAdoSettings
        {
            UserId = userId,
            OrganizationUrl = "https://dev.azure.com/org",
            DefaultProject = "Proj",
            EncryptedPat = writerProtector.Encrypt("real-pat-value"),
        });
        await dbContext.SaveChangesAsync();

        var service = new UserAdoConfigService(dbContext, readerProtector, new FakeAdoClient(), NullLogger<UserAdoConfigService>.Instance);

        var result = await service.TestConnectionAsync(userId);

        Assert.False(result.Success);
        Assert.Contains("re-enter your PAT", result.Error);
    }
}
