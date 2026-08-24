using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AzureBuddy.Core.Chat;

/// <summary>
/// Redis-backed IPendingAttachmentStore. Deliberately does NOT mirror RedisChatHistoryStore's shape:
///  - raw bytes, not a JSON envelope - ChatWindowSerialization's envelope exists to detect a garbled
///    payload as a cache miss for TEXT; base64-wrapping a multi-MB file in JSON costs ~30% size and a
///    full-string materialization for nothing RedisValue's native byte[] support doesn't already give.
///  - no optimistic-CAS/retry loop - that exists in RedisChatHistoryStore because two replicas can
///    concurrently APPEND to one conversation. A pending attachment is a single-writer slot (one user,
///    one upload button) - last-write-wins is correct and simpler.
/// </summary>
public sealed class RedisPendingAttachmentStore : IPendingAttachmentStore
{
    private static readonly RedisValue FileNameField = "fileName";
    private static readonly RedisValue ContentTypeField = "contentType";
    private static readonly RedisValue ContentField = "content";
    private static readonly RedisValue UploadedAtField = "uploadedAt";

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisOptions _redisOptions;
    private readonly TimeSpan _ttl;

    public RedisPendingAttachmentStore(IConnectionMultiplexer multiplexer, IOptions<RedisOptions> redisOptions, IOptions<AdoOptions> adoOptions)
    {
        _multiplexer = multiplexer;
        _redisOptions = redisOptions.Value;
        _ttl = TimeSpan.FromMinutes(adoOptions.Value.PendingAttachmentTtlMinutes);
    }

    public async Task SetAsync(string sessionId, PendingAttachment attachment, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var key = Key(sessionId);
        await db.HashSetAsync(key, new[]
        {
            new HashEntry(FileNameField, attachment.FileName),
            new HashEntry(ContentTypeField, attachment.ContentType),
            new HashEntry(ContentField, attachment.Content),
            new HashEntry(UploadedAtField, attachment.UploadedAtUtc.Ticks),
        });
        await db.KeyExpireAsync(key, _ttl);
    }

    public async Task<PendingAttachment?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var entries = await db.HashGetAsync(Key(sessionId), new[] { FileNameField, ContentTypeField, ContentField, UploadedAtField });

        if (entries.Length < 4 || entries[2].IsNullOrEmpty)
        {
            return null;
        }

        return new PendingAttachment(
            entries[0].ToString(),
            entries[1].ToString(),
            (byte[])entries[2]!,
            new DateTime((long)entries[3], DateTimeKind.Utc));
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _multiplexer.GetDatabase().KeyDeleteAsync(Key(sessionId));

    private string Key(string sessionId) => $"{_redisOptions.InstanceName}pending-attachment:{sessionId}";
}
