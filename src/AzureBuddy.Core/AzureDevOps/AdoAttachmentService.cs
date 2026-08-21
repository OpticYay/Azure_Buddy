using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.AzureDevOps;

public sealed class AdoAttachmentService : IAdoAttachmentService
{
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

        // Everything from here is a best-effort UX nicety layered on top of the attachment that
        // already succeeded above - failures are logged, never thrown, so a work item type that's
        // missing a field (e.g. no ReproSteps on a Task) can't turn a successful attach into an error.
        var evidenceHtml = BuildEvidenceHtml(attachment.Url);
        await TryEmbedInDescriptionAsync(connection, workItemId, evidenceHtml, cancellationToken);

        // Always post the full evidence (not just a "see above" pointer) as a comment, regardless of
        // whether the description embed above reported success. A 200 from that PATCH only proves ADO
        // accepted a write to *some* field - it doesn't prove that field is actually shown on this
        // work item type's form layout (which varies per org/process template and isn't something this
        // app can introspect). The comment is the one place guaranteed to be visible, so it must never
        // depend on the embed's apparent success.
        await TryAddEvidenceCommentAsync(connection, workItemId, evidenceHtml, cancellationToken);

        return attachment.Url;
    }

    private static string BuildEvidenceHtml(string attachmentUrl) =>
        "<b>Evidence:</b> Screenshot attached - see Attachments tab.<br>" +
        $"<img src=\"{attachmentUrl}\" alt=\"Screenshot evidence\" style=\"max-width:600px\" />";

    /// <summary>
    /// Tries to embed the screenshot inline in whichever body-like HTML field this work item type
    /// actually has. Bugs/Test Cases created via CreateBugFlow's boilerplate use ReproSteps (and may
    /// still have our own "Evidence: Not provided" placeholder to replace); every other type (Task,
    /// User Story, Feature, ...) generally only has System.Description. Returns false - never throws -
    /// if neither field exists for this work item type, so the caller can fall back to a comment.
    /// </summary>
    private async Task TryEmbedInDescriptionAsync(
        AdoConnectionContext connection,
        int workItemId,
        string evidenceHtml,
        CancellationToken cancellationToken)
    {
        var items = await _adoClient.GetWorkItemsAsync(
            connection, new[] { workItemId }, new[] { AdoFields.ReproSteps, AdoFields.Description }, cancellationToken);
        var item = items.FirstOrDefault();

        var (fieldName, current) = item?.ReproSteps is not null
            ? (AdoFields.ReproSteps, item.ReproSteps)
            : (AdoFields.Description, item?.Description ?? string.Empty);

        var updated = current.Contains(BugDescriptionTemplate.EvidencePlaceholder)
            ? current.Replace(BugDescriptionTemplate.EvidencePlaceholder, evidenceHtml)
            : string.IsNullOrEmpty(current)
                ? evidenceHtml
                : $"{current}<br><br>{evidenceHtml}";

        try
        {
            await _adoClient.UpdateWorkItemAsync(
                connection,
                workItemId,
                new[] { JsonPatchOperation.Add($"/fields/{fieldName}", updated) },
                cancellationToken);
            _logger.LogInformation("Embedded evidence into {Field} for work item {WorkItemId}.", fieldName, workItemId);
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Could not embed evidence into {Field} for work item {WorkItemId}.", fieldName, workItemId);
        }
    }

    /// <summary>
    /// Adds the full evidence (not just a pointer) as a work item comment (System.History). Unlike
    /// ReproSteps/Description, comments exist on every work item type and process template and are
    /// always shown on the work item's Discussion tab, so this - not the description embed above - is
    /// the one path guaranteed to actually surface the screenshot to a viewer.
    /// </summary>
    private async Task TryAddEvidenceCommentAsync(
        AdoConnectionContext connection,
        int workItemId,
        string evidenceHtml,
        CancellationToken cancellationToken)
    {
        try
        {
            await _adoClient.UpdateWorkItemAsync(
                connection,
                workItemId,
                new[] { JsonPatchOperation.Add($"/fields/{AdoFields.History}", evidenceHtml) },
                cancellationToken);
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Could not add evidence comment to work item {WorkItemId}.", workItemId);
        }
    }
}
