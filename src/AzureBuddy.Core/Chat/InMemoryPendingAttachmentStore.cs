using AzureBuddy.Core.AzureDevOps;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AzureBuddy.Core.Chat;

/// <summary>
/// In-memory IPendingAttachmentStore used whenever Redis isn't configured - single-process only, the
/// same limitation InMemoryChatHistoryStore/InMemoryChatWindowCache accept. Deliberately does NOT reuse
/// InMemoryChatWindowCache's shape: that cache sizes entries by SESSION COUNT (Size = 1 each), which is
/// fine for text conversation windows but would let a couple thousand 10 MB uploads reach a ~20 GB
/// ceiling. This one sizes by actual byte length against a byte-denominated SizeLimit, and expires on
/// an absolute clock rather than sliding - a pending upload shouldn't get a fresh TTL just because its
/// metadata was read while deciding which tool to call.
/// </summary>
public sealed class InMemoryPendingAttachmentStore : IPendingAttachmentStore
{
    private const long MaxTotalBytes = 200 * 1024 * 1024;

    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxTotalBytes });
    private readonly TimeSpan _ttl;

    public InMemoryPendingAttachmentStore(IOptions<AdoOptions> adoOptions)
    {
        _ttl = TimeSpan.FromMinutes(adoOptions.Value.PendingAttachmentTtlMinutes);
    }

    public Task SetAsync(string sessionId, PendingAttachment attachment, CancellationToken cancellationToken = default)
    {
        _cache.Set(sessionId, attachment, new MemoryCacheEntryOptions
        {
            Size = attachment.Content.Length,
            AbsoluteExpirationRelativeToNow = _ttl,
        });
        return Task.CompletedTask;
    }

    public Task<PendingAttachment?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cache.TryGetValue(sessionId, out PendingAttachment? attachment) ? attachment : null);

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _cache.Remove(sessionId);
        return Task.CompletedTask;
    }
}
