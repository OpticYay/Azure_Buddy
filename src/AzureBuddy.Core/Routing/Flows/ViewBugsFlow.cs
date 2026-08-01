using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Formatting;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Has Target Id (View)? -> Get Linked Bugs (View) -> Has Linked Items (View)? -> Get
/// Linked Bugs Details (View) -> Build View Table".</summary>
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

        var items = await _adoClient.GetWorkItemsAsync(
            connection,
            linkedIds,
            new[] { AdoFields.Title, AdoFields.WorkItemType, AdoFields.State },
            cancellationToken);

        var table = MarkdownTableBuilder.Build(
            new[] { "ID", "Title", "Type", "State" },
            items.Select(i => (IReadOnlyList<string>)new[] { i.Id.ToString(), i.Title ?? "", i.WorkItemType ?? "", i.State ?? "" }));

        return FlowResult.Done($"Work items linked to #{extracted.WorkItemId}:\n\n{table}");
    }
}
