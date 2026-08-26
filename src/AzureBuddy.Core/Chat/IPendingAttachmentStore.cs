namespace AzureBuddy.Core.Chat;

public sealed record PendingAttachment(string FileName, string ContentType, byte[] Content, DateTime UploadedAtUtc);

/// <summary>
/// Holds at most one uploaded-but-not-yet-attached file per chat session, bridging the gap between
/// "user dropped a file into the composer" (POST /api/chats/{id}/attachments) and "the agent decided
/// which work item to attach it to" (the attach_file_to_work_item tool, one or more turns later). A
/// second upload for the same session replaces the first - this is a single pending slot, not a queue.
/// </summary>
public interface IPendingAttachmentStore
{
    Task SetAsync(string sessionId, PendingAttachment attachment, CancellationToken cancellationToken = default);

    Task<PendingAttachment?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);
}
