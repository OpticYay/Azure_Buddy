using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Get My Work Items (Code Path) -> Has My Items? -> Get My Work Items Details (Code
/// Path) -> Build My Items Table". Results are ranked most-to-least urgent by
/// <see cref="WorkItemUrgencyRanker"/> rather than left in whatever order ADO returned them.</summary>
public sealed class MyItemsFlow
{
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

        var ranked = await AdoWorkItemQueries.GetAssignedToMeAsync(
            _adoClient, connection, extracted.State, extracted.WorkItemTypeFilter, sortByUrgency: true, cancellationToken);

        if (ranked.Count == 0)
        {
            var noun = string.IsNullOrEmpty(extracted.WorkItemTypeFilter) ? "work items" : $"{extracted.WorkItemTypeFilter} items";
            return FlowResult.Done($"No matching {noun} found assigned to you.");
        }

        var (headers, rows, table) = WorkItemTableBuilder.Build(ranked);

        var description = string.IsNullOrEmpty(extracted.WorkItemTypeFilter) ? "work items" : $"{extracted.WorkItemTypeFilter} work items";
        return FlowResult.DoneWithTable($"Your assigned {description}, most urgent first:\n\n{table}", headers, rows);
    }
}
