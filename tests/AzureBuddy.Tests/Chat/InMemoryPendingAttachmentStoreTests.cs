using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBuddy.Tests.Chat;

public class InMemoryPendingAttachmentStoreTests
{
    private static InMemoryPendingAttachmentStore NewStore(int ttlMinutes = 15) =>
        new(Options.Create(new AdoOptions { PendingAttachmentTtlMinutes = ttlMinutes }));

    [Fact]
    public async Task GetAsync_UnknownSession_ReturnsNull()
    {
        var store = NewStore();

        var result = await store.GetAsync("unknown-session");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_ThenGet_RoundTripsAttachment()
    {
        var store = NewStore();
        var attachment = new PendingAttachment("screenshot.png", "image/png", new byte[] { 1, 2, 3 }, DateTime.UtcNow);

        await store.SetAsync("session-1", attachment);
        var result = await store.GetAsync("session-1");

        Assert.NotNull(result);
        Assert.Equal("screenshot.png", result!.FileName);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(attachment.Content, result.Content);
    }

    [Fact]
    public async Task SetAsync_CalledTwiceForSameSession_ReplacesPreviousAttachment()
    {
        var store = NewStore();
        await store.SetAsync("session-1", new PendingAttachment("first.png", "image/png", new byte[] { 1 }, DateTime.UtcNow));
        await store.SetAsync("session-1", new PendingAttachment("second.png", "image/png", new byte[] { 2 }, DateTime.UtcNow));

        var result = await store.GetAsync("session-1");

        Assert.Equal("second.png", result!.FileName);
    }

    [Fact]
    public async Task RemoveAsync_DeletesPendingAttachment()
    {
        var store = NewStore();
        await store.SetAsync("session-1", new PendingAttachment("file.png", "image/png", new byte[] { 1 }, DateTime.UtcNow));

        await store.RemoveAsync("session-1");
        var result = await store.GetAsync("session-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_UnknownSession_DoesNotThrow()
    {
        var store = NewStore();

        await store.RemoveAsync("never-set");
    }

    [Fact]
    public async Task DifferentSessions_AreIsolatedFromEachOther()
    {
        var store = NewStore();
        await store.SetAsync("session-a", new PendingAttachment("a.png", "image/png", new byte[] { 1 }, DateTime.UtcNow));

        var resultB = await store.GetAsync("session-b");

        Assert.Null(resultB);
    }
}
