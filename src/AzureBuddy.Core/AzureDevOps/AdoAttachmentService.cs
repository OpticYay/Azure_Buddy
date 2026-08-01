namespace AzureBuddy.Core.AzureDevOps;

public sealed class AdoAttachmentService : IAdoAttachmentService
{
    private readonly IAdoClient _adoClient;

    public AdoAttachmentService(IAdoClient adoClient)
    {
        _adoClient = adoClient;
    }

    public async Task<string> AttachScreenshotAsync(
        AdoConnectionContext connection,
        int workItemId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var attachment = await _adoClient.CreateAttachmentAsync(connection, fileName, content, cancellationToken);

        // Link the uploaded attachment to the work item so it shows up on the ADO work item itself,
        // not just floating as an orphaned attachment. If this second call fails, the attachment still
        // exists in ADO but unlinked - an acceptable tradeoff over trying to make two HTTP calls
        // transactional (ADO's REST API doesn't support that).
        await _adoClient.UpdateWorkItemAsync(
            connection,
            workItemId,
            new[] { AdoRelationOps.AttachedFileLink(attachment.Url, "Screenshot attached via chat") },
            cancellationToken);

        return attachment.Url;
    }
}
