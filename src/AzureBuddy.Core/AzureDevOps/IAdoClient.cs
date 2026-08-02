namespace AzureBuddy.Core.AzureDevOps;

public interface IAdoClient
{
    /// <summary>Runs a WIQL query and returns the matching work item ids (WIQL only ever returns ids/refs).</summary>
    Task<IReadOnlyList<int>> QueryWiqlAsync(AdoConnectionContext connection, string wiqlQuery, CancellationToken cancellationToken = default);

    /// <summary>Fetches full field data for a set of work item ids in one batch call.</summary>
    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(
        AdoConnectionContext connection,
        IEnumerable<int> ids,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new work item of the given type (e.g. "Bug") via JSON-Patch document.</summary>
    Task<WorkItem> CreateWorkItemAsync(
        AdoConnectionContext connection,
        string workItemType,
        IReadOnlyList<JsonPatchOperation> operations,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an existing work item via JSON-Patch document (state change, comment, evidence link, etc.).</summary>
    Task<WorkItem> UpdateWorkItemAsync(
        AdoConnectionContext connection,
        int id,
        IReadOnlyList<JsonPatchOperation> operations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads raw bytes as a new ADO attachment and returns its URL. Used for the chat screenshot flow:
    /// the caller uploads the bytes here, gets a URL back, then (separately) links that URL to a work
    /// item via UpdateWorkItemAsync with an "AttachedFile" relation op - see ChatSessionService.
    /// The image bytes are never written to disk by this app; they only ever live in memory for the
    /// duration of this one call.
    /// </summary>
    Task<AdoAttachmentReference> CreateAttachmentAsync(
        AdoConnectionContext connection,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>Lightweight call used by "test connection": lists a page of projects, which requires a
    /// valid PAT with at least read access but doesn't mutate anything.</summary>
    Task<bool> TestConnectionAsync(AdoConnectionContext connection, CancellationToken cancellationToken = default);
}
