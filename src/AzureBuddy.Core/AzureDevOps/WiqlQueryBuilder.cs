using System.Text.RegularExpressions;

namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Centralizes the WIQL query shapes used across both ADO access paths in this app - the deterministic
/// flows (Routing/Flows/*) and the conversational agent's tools (Agent/AdoWorkItemToolset) - which
/// otherwise each built the same handful of queries independently and could drift out of sync.
/// </summary>
public static class WiqlQueryBuilder
{
    // Every query below is scoped to the caller's project. WIQL does NOT inherit project scope from the
    // project-scoped REST URL it's posted to - an unscoped query runs across the whole organization, so
    // a title search in a many-project org was matching (or failing on) work items the user never asked
    // about and the PAT may not even be able to read. @project is the ADO macro for "the project this
    // query is running in", which the REST endpoint's URL supplies.
    private const string ProjectScope = "[System.TeamProject] = @project";

    /// <summary>Open (non-Closed) work items whose title contains the given phrase as a substring.</summary>
    public static string SearchByTitle(string searchTerm) =>
        SearchByTitleClause($"[System.Title] CONTAINS '{Escape(searchTerm)}'");

    /// <summary>
    /// Same search widened to match each word of the phrase independently, in any order - "login screen"
    /// also finds "Screen: login redesign". CONTAINS is a plain substring test, so the single-phrase form
    /// only matches titles that contain those words adjacent and in that exact order, which is a poor fit
    /// for phrases an LLM paraphrased out of a user's sentence. Used as a fallback when the phrase match
    /// finds nothing (see AdoWorkItemToolset.SearchWorkItemsAsync).
    /// Note this stays CONTAINS rather than CONTAINS WORDS: System.Title is a String field, and per ADO's
    /// operator table CONTAINS WORDS is only valid on PlainText/HTML fields - using it here is a 400.
    /// </summary>
    public static string SearchByTitleWords(IEnumerable<string> words) =>
        SearchByTitleClause(string.Join(" AND ", words.Select(w => $"[System.Title] CONTAINS '{Escape(w)}'")));

    private static string SearchByTitleClause(string titleClause) =>
        $"SELECT [System.Id], [System.Title], [System.WorkItemType] FROM WorkItems " +
        $"WHERE {ProjectScope} AND ({titleClause}) AND [System.State] <> 'Closed' " +
        $"ORDER BY [System.CreatedDate] DESC";

    /// <summary>Splits a search phrase into the words worth matching on - drops punctuation and the
    /// work-item-vocabulary noise ("user story", "bug", "workitem") that users say as part of the
    /// sentence but that is rarely in the title itself.</summary>
    public static IReadOnlyList<string> SearchWords(string searchTerm) =>
        Regex.Split(searchTerm ?? string.Empty, @"[^\w]+")
            .Where(w => w.Length > 1 && !NoiseWords.Contains(w))
            .ToList();

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "for", "of", "in", "on", "under", "and", "to",
        "user", "story", "stories", "bug", "bugs", "task", "tasks",
        "workitem", "workitems", "work", "item", "items", "get", "me", "all"
    };

    /// <summary>Direct children of a given parent work item.</summary>
    public static string ChildrenOf(int parentId) =>
        $"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State], [System.CreatedDate] " +
        $"FROM WorkItems WHERE {ProjectScope} AND [System.Parent] = {parentId} ORDER BY [System.CreatedDate] DESC";

    /// <summary>Work items assigned to the PAT owner, optionally filtered to one state (otherwise
    /// everything not Closed).</summary>
    public static string AssignedToMe(string? state) =>
        $"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State] FROM WorkItems " +
        $"WHERE {ProjectScope} AND [System.AssignedTo] = @Me {StateFilter(state)} ORDER BY [System.ChangedDate] DESC";

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
