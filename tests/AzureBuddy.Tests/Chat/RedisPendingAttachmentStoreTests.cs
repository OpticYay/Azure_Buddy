using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Caching;
using AzureBuddy.Core.Chat;
using AzureBuddy.Tests.Integration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace AzureBuddy.Tests.Chat;

/// <summary>Runs against a real Redis container (see SharedRedisContainer), mirroring
/// RedisChatHistoryStoreTests - the hash read/write shape is worth exercising against the real server.</summary>
public class RedisPendingAttachmentStoreTests : IAsyncLifetime
{
    private IConnectionMultiplexer _multiplexer = null!;
    private string _instanceName = null!;

    public async Task InitializeAsync()
    {
        var container = await SharedRedisContainer.GetAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        _instanceName = $"test:{Guid.NewGuid()}:";
    }

    public Task DisposeAsync()
    {
        _multiplexer.Dispose();
        return Task.CompletedTask;
    }

    private RedisPendingAttachmentStore NewStore(int ttlMinutes = 15) => new(
        _multiplexer,
        Options.Create(new RedisOptions { InstanceName = _instanceName }),
        Options.Create(new AdoOptions { PendingAttachmentTtlMinutes = ttlMinutes }));

    [Fact]
    public async Task GetAsync_UnknownSession_ReturnsNull()
    {
        var store = NewStore();

        var result = await store.GetAsync(Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_ThenGet_RoundTripsAttachment()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid().ToString();
        var uploadedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var attachment = new PendingAttachment("screenshot.png", "image/png", new byte[] { 1, 2, 3, 4 }, uploadedAt);

        await store.SetAsync(sessionId, attachment);
        var result = await store.GetAsync(sessionId);

        Assert.NotNull(result);
        Assert.Equal("screenshot.png", result!.FileName);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(attachment.Content, result.Content);
        Assert.Equal(uploadedAt, result.UploadedAtUtc);
    }

    [Fact]
    public async Task SetAsync_CalledTwiceForSameSession_ReplacesPreviousAttachment()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid().ToString();

        await store.SetAsync(sessionId, new PendingAttachment("first.png", "image/png", new byte[] { 1 }, DateTime.UtcNow));
        await store.SetAsync(sessionId, new PendingAttachment("second.png", "image/png", new byte[] { 2 }, DateTime.UtcNow));

        var result = await store.GetAsync(sessionId);

        Assert.Equal("second.png", result!.FileName);
    }

    [Fact]
    public async Task RemoveAsync_DeletesPendingAttachment()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid().ToString();
        await store.SetAsync(sessionId, new PendingAttachment("file.png", "image/png", new byte[] { 1 }, DateTime.UtcNow));

        await store.RemoveAsync(sessionId);
        var result = await store.GetAsync(sessionId);

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_UnknownSession_DoesNotThrow()
    {
        var store = NewStore();

        await store.RemoveAsync(Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task DifferentInstanceNames_AreIsolatedFromEachOther()
    {
        var sessionId = Guid.NewGuid().ToString();
        var storeA = NewStore();
        var storeB = new RedisPendingAttachmentStore(
            _multiplexer,
            Options.Create(new RedisOptions { InstanceName = $"test:{Guid.NewGuid()}:" }),
            Options.Create(new AdoOptions()));

        await storeA.SetAsync(sessionId, new PendingAttachment("a.png", "image/png", new byte[] { 1 }, DateTime.UtcNow));
        var resultFromB = await storeB.GetAsync(sessionId);

        Assert.Null(resultFromB);
    }
}
