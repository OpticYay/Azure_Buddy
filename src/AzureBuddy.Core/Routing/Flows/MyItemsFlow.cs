using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Formatting;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Get My Work Items (Code Path) -> Has My Items? -> Get My Work Items Details (Code
/// Path) -> Build My Items Table".</summary>
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

        var stateFilter = string.IsNullOrEmpty(extracted.State)
            ? "AND [System.State] <> 'Closed'"
            : $"AND [System.State] = '{EscapeWiql(extracted.State)}'";

        var ids = await _adoClient.QueryWiqlAsync(
            connection,
            $"SELECT [System.Id] FROM WorkItems WHERE [System.AssignedTo] = @Me {stateFilter} ORDER BY [System.ChangedDate] DESC",
            cancellationToken);

        if (ids.Count == 0)
        {
            return FlowResult.Done("No matching work items found assigned to you.");
        }

        var items = await _adoClient.GetWorkItemsAsync(
            connection,
            ids,
            new[] { AdoFields.Title, AdoFields.WorkItemType, AdoFields.State },
            cancellationToken);

        var table = MarkdownTableBuilder.Build(
            new[] { "ID", "Title", "Type", "State" },
            items.Select(i => (IReadOnlyList<string>)new[] { i.Id.ToString(), i.Title ?? "", i.WorkItemType ?? "", i.State ?? "" }));

        return FlowResult.Done($"Your assigned work items:\n\n{table}");
    }

    private static string EscapeWiql(string value) => value.Replace("'", "''");
}
