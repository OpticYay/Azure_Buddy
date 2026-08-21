using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Tests.Integration;
using Xunit;

namespace AzureBuddy.Tests.Routing;

public class GetPrioritizedWorkItemsFlowTests
{
    private static AdoConnectionContextAccessor NewConnectionAccessor() => new()
    {
        Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
    };

    [Fact]
    public async Task ExecuteAsync_NoAssignedItems_ReturnsPlainMessageMentioningNoItemsToPrioritize()
    {
        var adoClient = new FakeAdoClient(); // default QueryWiql returns no ids
        var flow = new GetPrioritizedWorkItemsFlow(adoClient, NewConnectionAccessor());

        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.PrioritizeWorkItems }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("You have no assigned work items to prioritize.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_NoAssignedItemsWithTypeFilter_MentionsTheTypeInTheMessage()
    {
        var adoClient = new FakeAdoClient();
        var flow = new GetPrioritizedWorkItemsFlow(adoClient, NewConnectionAccessor());

        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.PrioritizeWorkItems, WorkItemTypeFilter = "Bug" }, CancellationToken.None);

        Assert.Equal("You have no assigned Bug items to prioritize.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ItemsFound_ReturnsSummaryLineAndTableSortedMostUrgentFirst()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 1, 2, 3 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => new List<WorkItem>
        {
            new() { Id = 1, Fields = new Dictionary<string, object?> { ["System.Title"] = "No due date", ["System.WorkItemType"] = "Task", ["System.State"] = "Active" } },
            new() { Id = 2, Fields = new Dictionary<string, object?> { ["System.Title"] = "Overdue", ["System.WorkItemType"] = "Bug", ["System.State"] = "Active", ["Microsoft.VSTS.Scheduling.DueDate"] = DateTime.UtcNow.AddDays(-3).ToString("O") } },
            new() { Id = 3, Fields = new Dictionary<string, object?> { ["System.Title"] = "Due next week", ["System.WorkItemType"] = "Task", ["System.State"] = "Active", ["Microsoft.VSTS.Scheduling.DueDate"] = DateTime.UtcNow.AddDays(3).ToString("O") } },
        };

        var flow = new GetPrioritizedWorkItemsFlow(adoClient, NewConnectionAccessor());
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.PrioritizeWorkItems }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Contains("1 overdue item", result.Output);
        Assert.Contains("due within the next 7 days", result.Output);
        Assert.Equal("2", result.TableRows![0][0]); // overdue item ranked first
    }

    [Fact]
    public async Task ExecuteAsync_NoOverdueOrDueSoonItems_SummaryStatesThatPlainly()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 1 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => new List<WorkItem>
        {
            new() { Id = 1, Fields = new Dictionary<string, object?> { ["System.Title"] = "Someday item", ["System.WorkItemType"] = "Task", ["System.State"] = "Active" } },
        };

        var flow = new GetPrioritizedWorkItemsFlow(adoClient, NewConnectionAccessor());
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.PrioritizeWorkItems }, CancellationToken.None);

        Assert.Contains("none are overdue or due within the next 7 days", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_PassesStateAndWorkItemTypeFilterIntoTheWiqlQuery()
    {
        string? capturedQuery = null;
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) => { capturedQuery = wiql; return Array.Empty<int>(); };

        var flow = new GetPrioritizedWorkItemsFlow(adoClient, NewConnectionAccessor());
        await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.PrioritizeWorkItems, State = "Active", WorkItemTypeFilter = "Bug" }, CancellationToken.None);

        Assert.Contains("[System.State] = 'Active'", capturedQuery);
        Assert.Contains("[System.WorkItemType] = 'Bug'", capturedQuery);
    }
}
