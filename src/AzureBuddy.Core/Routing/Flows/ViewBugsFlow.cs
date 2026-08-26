using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Has Target Id (View)? -> Get Linked Bugs (View) -> Has Linked Items (View)? -> Get
/// Linked Bugs Details (View) -> Build View Table". Results are ranked most-to-least urgent by
/// <see cref="WorkItemUrgencyRanker"/> rather than left in whatever order ADO returned them.</summary>
public sealed class ViewBugsFlow
{
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

        var items = await _adoClient.GetWorkItemsAsync(connection, linkedIds, WorkItemTableBuilder.Fields, cancellationToken);
        var ranked = WorkItemUrgencyRanker.SortByUrgency(items);

        var (headers, rows, table) = WorkItemTableBuilder.Build(ranked);

        return FlowResult.DoneWithTable($"Work items linked to #{extracted.WorkItemId}, most urgent first:\n\n{table}", headers, rows);
    }
}
