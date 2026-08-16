using AzureBuddy.Core.Caching;
using AzureBuddy.Tests.Integration;
using StackExchange.Redis;
using Xunit;

namespace AzureBuddy.Tests.Caching;

/// <summary>Runs against a real Redis container (see SharedRedisContainer) - the whole point is proving
/// keys land in the exact list shape PersistKeysToStackExchangeRedis's RedisXmlRepository reads from.</summary>
public class DataProtectionKeyImporterTests : IAsyncLifetime
{
    private const string SampleKeyXml =
        """<key id="{0}" version="1"><creationDate>2024-01-01T00:00:00Z</creationDate></key>""";

    private IConnectionMultiplexer _multiplexer = null!;
    private string _redisKey = null!;

    public async Task InitializeAsync()
    {
        var container = await SharedRedisContainer.GetAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        _redisKey = $"test:{Guid.NewGuid():N}:DataProtection-Keys";
    }

    public Task DisposeAsync()
    {
        _multiplexer.Dispose();
        return Task.CompletedTask;
    }

    private static string WriteKeyFile(string directory, Guid id)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"key-{id}.xml");
        File.WriteAllText(path, string.Format(SampleKeyXml, id));
        return path;
    }

    [Fact]
    public async Task ImportAsync_CopiesEachFilesystemKeyIntoTheRedisList()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"azurebuddy-dp-import-{Guid.NewGuid():N}");
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        WriteKeyFile(directory, idA);
        WriteKeyFile(directory, idB);

        var imported = await DataProtectionKeyImporter.ImportAsync(directory, _multiplexer, _redisKey);

        Assert.Equal(2, imported);
        var stored = await _multiplexer.GetDatabase().ListRangeAsync(_redisKey);
        Assert.Equal(2, stored.Length);
        Assert.Contains(stored, v => ((string)v!).Contains(idA.ToString()));
        Assert.Contains(stored, v => ((string)v!).Contains(idB.ToString()));
    }

    [Fact]
    public async Task ImportAsync_RunTwice_DoesNotDuplicateAlreadyImportedKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"azurebuddy-dp-import-{Guid.NewGuid():N}");
        WriteKeyFile(directory, Guid.NewGuid());

        await DataProtectionKeyImporter.ImportAsync(directory, _multiplexer, _redisKey);
        var secondRunImported = await DataProtectionKeyImporter.ImportAsync(directory, _multiplexer, _redisKey);

        Assert.Equal(0, secondRunImported);
        var stored = await _multiplexer.GetDatabase().ListRangeAsync(_redisKey);
        Assert.Single(stored);
    }

    [Fact]
    public async Task ImportAsync_DirectoryDoesNotExist_ReturnsZeroWithoutThrowing()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"azurebuddy-dp-import-missing-{Guid.NewGuid():N}");

        var imported = await DataProtectionKeyImporter.ImportAsync(missingDirectory, _multiplexer, _redisKey);

        Assert.Equal(0, imported);
    }
}
