using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Formatting;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Get My Work Items (Code Path) -> Has My Items? -> Get My Work Items Details (Code
/// Path) -> Build My Items Table". Results are ranked most-to-least urgent by
/// <see cref="WorkItemUrgencyRanker"/> rather than left in whatever order ADO returned them.</summary>
public sealed class MyItemsFlow
{
    private static readonly string[] Fields =
    {
        AdoFields.Title, AdoFields.WorkItemType, AdoFields.State,
        AdoFields.Priority, AdoFields.StartDate, AdoFields.TargetDate, AdoFields.DueDate, AdoFields.FinishDate
    };

    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;

    public MyItemsFlow(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
    }

    public async Task<FlowResult> ExecuteAsync(ExtractedIntent extracted, CancellationToken cancellationToken = default)
    {
        var connection = _connectionAccessor.Require();

        var ids = await _adoClient.QueryWiqlAsync(
            connection,
            WiqlQueryBuilder.AssignedToMe(extracted.State, extracted.WorkItemTypeFilter),
            cancellationToken);

        if (ids.Count == 0)
        {
            var noun = string.IsNullOrEmpty(extracted.WorkItemTypeFilter) ? "work items" : $"{extracted.WorkItemTypeFilter} items";
            return FlowResult.Done($"No matching {noun} found assigned to you.");
        }

        var items = await _adoClient.GetWorkItemsAsync(connection, ids, Fields, cancellationToken);
        var ranked = WorkItemUrgencyRanker.SortByUrgency(items);

        var headers = new[] { "ID", "Title", "Type", "State", "Priority", "Start Date", "Due Date" };
        var rows = ranked.Select(BuildRow).ToList();
        var table = MarkdownTableBuilder.Build(headers, rows);

        var description = string.IsNullOrEmpty(extracted.WorkItemTypeFilter) ? "work items" : $"{extracted.WorkItemTypeFilter} work items";
        return FlowResult.DoneWithTable($"Your assigned {description}, most urgent first:\n\n{table}", headers, rows);
    }

    private static IReadOnlyList<string> BuildRow(WorkItem i) => new[]
    {
        i.Id.ToString(), i.Title ?? "", i.WorkItemType ?? "", i.State ?? "",
        WorkItemUrgencyRanker.FormatPriority(i), WorkItemUrgencyRanker.FormatDate(i.StartDate), WorkItemUrgencyRanker.FormatDate(i.DueDate)
    };
}
