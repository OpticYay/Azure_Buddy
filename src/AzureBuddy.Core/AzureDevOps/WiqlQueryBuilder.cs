namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Centralizes the WIQL query shapes used across both ADO access paths in this app - the deterministic
/// flows (Routing/Flows/*) and the conversational agent's tools (Agent/AdoWorkItemToolset) - which
/// otherwise each built the same handful of queries independently and could drift out of sync.
/// </summary>
public static class WiqlQueryBuilder
{
    /// <summary>Open (non-Closed) work items whose title contains the given term.</summary>
    public static string SearchByTitle(string searchTerm) =>
        $"SELECT [System.Id], [System.Title], [System.WorkItemType] FROM WorkItems " +
        $"WHERE [System.Title] CONTAINS '{Escape(searchTerm)}' AND [System.State] <> 'Closed' " +
        $"ORDER BY [System.CreatedDate] DESC";

    /// <summary>Direct children of a given parent work item.</summary>
    public static string ChildrenOf(int parentId) =>
        $"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State], [System.CreatedDate] " +
        $"FROM WorkItems WHERE [System.Parent] = {parentId} ORDER BY [System.CreatedDate] DESC";

    /// <summary>Work items assigned to the PAT owner, optionally filtered to one state (otherwise
    /// everything not Closed).</summary>
    public static string AssignedToMe(string? state) =>
        $"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State] FROM WorkItems " +
        $"WHERE [System.AssignedTo] = @Me {StateFilter(state)} ORDER BY [System.ChangedDate] DESC";

    private static string StateFilter(string? state) =>
        string.IsNullOrEmpty(state)
            ? "AND [System.State] <> 'Closed'"
            : $"AND [System.State] = '{Escape(state)}'";

    /// <summary>Doubles single quotes so a user-supplied value can't break out of a WIQL string
    /// literal - WIQL's equivalent of parameterizing a SQL query (WIQL itself has no parameter syntax).</summary>
    public static string Escape(string? value) => (value ?? string.Empty).Replace("'", "''");
}
