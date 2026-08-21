namespace AzureBuddy.Core.AzureDevOps;

/// <summary>Shared "fetch my assigned work items, optionally ranked by urgency" logic - previously
/// duplicated across MyItemsFlow/GetPrioritizedWorkItemsFlow (Routing/Flows/) and
/// AdoWorkItemToolset.GetMyWorkItemsAsync/GetPrioritizedWorkItemsAsync.</summary>
public static class AdoWorkItemQueries
{
    public static async Task<IReadOnlyList<WorkItem>> GetAssignedToMeAsync(
        IAdoClient adoClient,
        AdoConnectionContext connection,
        string? state,
        string? workItemTypeFilter,
        bool sortByUrgency,
        CancellationToken cancellationToken)
    {
        var ids = await adoClient.QueryWiqlAsync(
            connection,
            WiqlQueryBuilder.AssignedToMe(state, workItemTypeFilter),
            cancellationToken);

        if (ids.Count == 0)
        {
            return Array.Empty<WorkItem>();
        }

        var items = await adoClient.GetWorkItemsAsync(connection, ids, WorkItemTableBuilder.Fields, cancellationToken);
        return sortByUrgency ? WorkItemUrgencyRanker.SortByUrgency(items) : items;
    }

    /// <summary>Two-stage title search: try the phrase as typed first (an exact substring hit is the
    /// most precise answer), and only if that finds nothing, widen to matching each word of the phrase
    /// independently. Shared by AdoWorkItemToolset.SearchWorkItemsAsync and CreateBugFlow's parent
    /// search, which both need this same widening behavior.</summary>
    public static async Task<IReadOnlyList<int>> SearchIdsByTitleAsync(
        IAdoClient adoClient,
        AdoConnectionContext connection,
        string searchTerm,
        CancellationToken cancellationToken)
    {
        var ids = await adoClient.QueryWiqlAsync(connection, WiqlQueryBuilder.SearchByTitle(searchTerm), cancellationToken);
        if (ids.Count > 0)
        {
            return ids;
        }

        var words = WiqlQueryBuilder.SearchWords(searchTerm);
        return words.Count > 0
            ? await adoClient.QueryWiqlAsync(connection, WiqlQueryBuilder.SearchByTitleWords(words), cancellationToken)
            : ids;
    }
}
