using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Tests.Integration;
using Xunit;

namespace AzureBuddy.Tests.Routing;

/// <summary>Regression coverage for a real bug: "get me all the bugs assigned to me" was returning
/// every work item type, not just Bugs, because MyItemsFlow never passed a type filter into the WIQL
/// query - see WiqlQueryBuilder.AssignedToMe's work_item_type parameter.</summary>
public class MyItemsFlowTests
{
    private static AdoConnectionContextAccessor NewConnectionAccessor() => new()
    {
        Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
    };

    [Fact]
    public async Task ExecuteAsync_WorkItemTypeFilterSet_PassesItIntoTheWiqlQuery()
    {
        string? capturedQuery = null;
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) =>
        {
            capturedQuery = wiql;
            return new[] { 42 };
        };
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?> { ["System.Title"] = "A bug", ["System.WorkItemType"] = "Bug", ["System.State"] = "Active" },
            }).ToList();

        var flow = new MyItemsFlow(adoClient, NewConnectionAccessor());
        var extracted = new ExtractedIntent { Intent = ChatIntent.MyItems, WorkItemTypeFilter = "Bug" };

        await flow.ExecuteAsync(extracted, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Contains("[System.WorkItemType] = 'Bug'", capturedQuery);
    }

    [Fact]
    public async Task ExecuteAsync_NoWorkItemTypeFilter_QueryHasNoTypeClause()
    {
        string? capturedQuery = null;
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) =>
        {
            capturedQuery = wiql;
            return Array.Empty<int>();
        };

        var flow = new MyItemsFlow(adoClient, NewConnectionAccessor());
        var extracted = new ExtractedIntent { Intent = ChatIntent.MyItems };

        await flow.ExecuteAsync(extracted, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.DoesNotContain("[System.WorkItemType] =", capturedQuery);
    }

    [Fact]
    public async Task ExecuteAsync_TypeFilteredWithNoResults_MentionsTheTypeInTheMessage()
    {
        var adoClient = new FakeAdoClient(); // default QueryWiqlBehavior returns no ids
        var flow = new MyItemsFlow(adoClient, NewConnectionAccessor());
        var extracted = new ExtractedIntent { Intent = ChatIntent.MyItems, WorkItemTypeFilter = "Bug" };

        var result = await flow.ExecuteAsync(extracted, CancellationToken.None);

        Assert.Equal("No matching Bug items found assigned to you.", result.Output);
    }
}
