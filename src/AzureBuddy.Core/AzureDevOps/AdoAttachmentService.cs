using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.AzureDevOps;

public sealed class AdoAttachmentService : IAdoAttachmentService
{
    private const string EvidencePlaceholder = "<b>Evidence:</b> Not provided";

    private readonly IAdoClient _adoClient;
    private readonly ILogger<AdoAttachmentService> _logger;

    public AdoAttachmentService(IAdoClient adoClient, ILogger<AdoAttachmentService> logger)
    {
        _adoClient = adoClient;
        _logger = logger;
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

        // Best-effort: also surface the screenshot in the Evidence section of the boilerplate
        // description (Microsoft.VSTS.TCM.ReproSteps - see CreateBugFlow.BuildDescriptionHtml) so a
        // reader doesn't have to know to check the Attachments tab. Deliberately swallowed on failure
        // (e.g. work item type has no ReproSteps field) - the attachment itself already succeeded
        // above, and this is a UX nicety layered on top, not a required step.
        try
        {
            await AppendEvidenceNoteAsync(connection, workItemId, attachment.Url, cancellationToken);
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Could not append evidence note to work item {WorkItemId} after screenshot attach.", workItemId);
        }

        return attachment.Url;
    }

    private async Task AppendEvidenceNoteAsync(
        AdoConnectionContext connection,
        int workItemId,
        string attachmentUrl,
        CancellationToken cancellationToken)
    {
        var items = await _adoClient.GetWorkItemsAsync(connection, new[] { workItemId }, new[] { AdoFields.ReproSteps }, cancellationToken);
        var current = items.FirstOrDefault()?.ReproSteps ?? string.Empty;

        // Embed the screenshot inline (ADO renders <img> in its HTML-type fields), plus a plain-text
        // pointer to the Attachments tab in case the embed doesn't render for some reason (e.g. a
        // client that doesn't fetch inline images) - covers both preferences at once.
        var evidenceHtml =
            "<b>Evidence:</b> Screenshot attached - see Attachments tab.<br>" +
            $"<img src=\"{attachmentUrl}\" alt=\"Screenshot evidence\" style=\"max-width:600px\" />";

        var updated = current.Contains(EvidencePlaceholder)
            ? current.Replace(EvidencePlaceholder, evidenceHtml)
            : string.IsNullOrEmpty(current)
                ? evidenceHtml
                : $"{current}<br><br>{evidenceHtml}";

        await _adoClient.UpdateWorkItemAsync(
            connection,
            workItemId,
            new[] { JsonPatchOperation.Add($"/fields/{AdoFields.ReproSteps}", updated) },
            cancellationToken);
    }
}
