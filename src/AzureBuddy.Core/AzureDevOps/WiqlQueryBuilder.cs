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

    // "open" is how people ask for "not finished yet" in plain English, but it isn't an actual ADO
    // state on any standard process template (states are things like New/Active/Resolved/Closed) - so
    // treating it as a literal state to match returns zero rows every time, not "everything open" as
    // asked. Fold it into the same "not Closed" default the no-state case already uses.
    private static string StateFilter(string? state) =>
        string.IsNullOrEmpty(state) || state.Trim().Equals("open", StringComparison.OrdinalIgnoreCase)
            ? "AND [System.State] <> 'Closed'"
            : $"AND [System.State] = '{Escape(state)}'";

    /// <summary>Doubles single quotes so a user-supplied value can't break out of a WIQL string
    /// literal - WIQL's equivalent of parameterizing a SQL query (WIQL itself has no parameter syntax).</summary>
    public static string Escape(string? value) => (value ?? string.Empty).Replace("'", "''");
}
