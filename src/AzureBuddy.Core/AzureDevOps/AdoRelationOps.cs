namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// ADO work item "relation" (link) types as named constants instead of scattered string literals -
/// a typo in one of these produces a malformed link that ADO silently accepts differently than
/// intended, with no compile-time signal. Paired with builder methods for the JsonPatchOperation
/// shape each relation type needs, since that shape was previously hand-built inline in 4 places.
/// </summary>
public static class AdoRelationOps
{
    private static class RelationTypes
    {
        public const string HierarchyReverse = "System.LinkTypes.Hierarchy-Reverse";
        public const string Hyperlink = "Hyperlink";
        public const string AttachedFile = "AttachedFile";
    }

    /// <summary>Links a new work item to its parent (used when creating a bug under a User Story/Task).</summary>
    public static JsonPatchOperation ParentLink(string parentWorkItemUrl) =>
        JsonPatchOperation.Add("/relations/-", new
        {
            rel = RelationTypes.HierarchyReverse,
            url = parentWorkItemUrl
        });

    /// <summary>Attaches an external URL (e.g. a screenshot/log already hosted elsewhere) as evidence.</summary>
    public static JsonPatchOperation EvidenceLink(string evidenceUrl, string comment) =>
        JsonPatchOperation.Add("/relations/-", new
        {
            rel = RelationTypes.Hyperlink,
            url = evidenceUrl,
            attributes = new { comment }
        });

    /// <summary>Links an ADO-hosted attachment (uploaded via IAdoClient.CreateAttachmentAsync) to a work item.</summary>
    public static JsonPatchOperation AttachedFileLink(string attachmentUrl, string comment) =>
        JsonPatchOperation.Add("/relations/-", new
        {
            rel = RelationTypes.AttachedFile,
            url = attachmentUrl,
            attributes = new { comment }
        });
}
