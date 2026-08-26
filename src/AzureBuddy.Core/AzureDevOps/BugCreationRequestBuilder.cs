namespace AzureBuddy.Core.AzureDevOps;

/// <summary>Shared JSON-patch assembly for creating a bug linked to a parent - previously duplicated
/// between AdoWorkItemToolset.CreateLinkedBugAsync and CreateBugFlow.CreateBugAsync, including the
/// same parent-link URL construction and the same optional-field handling.</summary>
public static class BugCreationRequestBuilder
{
    public static string ParentWorkItemUrl(AdoConnectionContext connection, string parentId) =>
        $"{connection.OrganizationUrl.TrimEnd('/')}/{connection.Project}/_apis/wit/workItems/{parentId}";

    public static List<JsonPatchOperation> BuildOps(
        AdoConnectionContext connection,
        string title,
        string description,
        string parentId,
        string? priority = null,
        string? severity = null,
        string? areaPath = null,
        string? iterationPath = null,
        string? assignedTo = null)
    {
        var ops = new List<JsonPatchOperation>
        {
            JsonPatchOperation.Add($"/fields/{AdoFields.Title}", title),
            JsonPatchOperation.Add($"/fields/{AdoFields.ReproSteps}", description),
            AdoRelationOps.ParentLink(ParentWorkItemUrl(connection, parentId))
        };

        AddOptionalField(ops, AdoFields.Priority, priority);
        AddOptionalField(ops, AdoFields.Severity, severity);
        AddOptionalField(ops, AdoFields.AreaPath, areaPath);
        AddOptionalField(ops, AdoFields.IterationPath, iterationPath);
        AddOptionalField(ops, AdoFields.AssignedTo, assignedTo);

        return ops;
    }

    private static void AddOptionalField(List<JsonPatchOperation> ops, string field, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ops.Add(JsonPatchOperation.Add($"/fields/{field}", value));
        }
    }
}
