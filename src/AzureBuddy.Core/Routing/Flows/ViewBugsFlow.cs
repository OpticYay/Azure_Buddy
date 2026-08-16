using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Formatting;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Has Target Id (View)? -> Get Linked Bugs (View) -> Has Linked Items (View)? -> Get
/// Linked Bugs Details (View) -> Build View Table". Results are ranked most-to-least urgent by
/// <see cref="WorkItemUrgencyRanker"/> rather than left in whatever order ADO returned them.</summary>
public sealed class ViewBugsFlow
{
    private static readonly string[] Fields =
    {
        AdoFields.Title, AdoFields.WorkItemType, AdoFields.State,
        AdoFields.Priority, AdoFields.StartDate, AdoFields.TargetDate, AdoFields.DueDate, AdoFields.FinishDate
    };

    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;

    public ViewBugsFlow(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
    }

    public async Task<FlowResult> ExecuteAsync(ExtractedIntent extracted, CancellationToken cancellationToken = default)
    {
        // int.TryParse (not int.Parse) here defensively - WorkItemId is digits-only by construction
        // (IntentExtractor strips non-digits), but an overflow on an implausibly long id string
        // shouldn't throw an uncaught FormatException/OverflowException through this flow.
        if (!int.TryParse(extracted.WorkItemId, out var workItemId))
        {
            return FlowResult.FallThroughToAgent();
        }

        var connection = _connectionAccessor.Require();

        var linkedIds = await _adoClient.QueryWiqlAsync(
            connection,
            WiqlQueryBuilder.ChildrenOf(workItemId),
            cancellationToken);

        if (linkedIds.Count == 0)
        {
            return FlowResult.Done($"No linked work items found under #{extracted.WorkItemId}.");
        }

        var items = await _adoClient.GetWorkItemsAsync(connection, linkedIds, Fields, cancellationToken);
        var ranked = WorkItemUrgencyRanker.SortByUrgency(items);

        var headers = new[] { "ID", "Title", "Type", "State", "Priority", "Start Date", "Due Date" };
        var rows = ranked.Select(BuildRow).ToList();
        var table = MarkdownTableBuilder.Build(headers, rows);

        return FlowResult.DoneWithTable($"Work items linked to #{extracted.WorkItemId}, most urgent first:\n\n{table}", headers, rows);
    }

    private static IReadOnlyList<string> BuildRow(WorkItem i) => new[]
    {
        i.Id.ToString(), i.Title ?? "", i.WorkItemType ?? "", i.State ?? "",
        WorkItemUrgencyRanker.FormatPriority(i), WorkItemUrgencyRanker.FormatDate(i.StartDate), WorkItemUrgencyRanker.FormatDate(i.DueDate)
    };
}
