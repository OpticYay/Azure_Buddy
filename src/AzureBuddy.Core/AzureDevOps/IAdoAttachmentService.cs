namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Uploading a screenshot to ADO is really two calls (create the attachment, then link it to a work
/// item) plus knowledge of the "AttachedFile" relation shape. That's ADO-API knowledge, not chat
/// persistence knowledge - split out from ChatSessionService (which should only know "append this
/// message", not "here's the JSON-Patch shape for an ADO attachment link") so each class has one
/// reason to change.
/// </summary>
public interface IAdoAttachmentService
{
    /// <summary>Uploads the given bytes as a new ADO attachment and links it to the work item in one
    /// call. Returns the attachment's URL on success. Throws AdoApiException if either the upload or
    /// the link step fails - callers decide how to surface that (e.g. ChatSessionService turns it into
    /// an error message in the chat).</summary>
    Task<string> AttachScreenshotAsync(
        AdoConnectionContext connection,
        int workItemId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default);
}
