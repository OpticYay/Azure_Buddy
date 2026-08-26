using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Tests.Integration;
using Xunit;

namespace AzureBuddy.Tests.Routing;

public class ViewBugsFlowTests
{
    private static AdoConnectionContextAccessor NewConnectionAccessor() => new()
    {
        Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
    };

    [Fact]
    public async Task ExecuteAsync_NonNumericWorkItemId_FallsThroughToAgent()
    {
        var adoClient = new FakeAdoClient();
        var flow = new ViewBugsFlow(adoClient, NewConnectionAccessor());

        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.ViewBugs, WorkItemId = "" }, CancellationToken.None);

        Assert.False(result.Handled);
        Assert.Empty(adoClient.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_NoLinkedItems_ReturnsDoneMessageNotAFallThrough()
    {
        var adoClient = new FakeAdoClient(); // default QueryWiql returns no ids
        var flow = new ViewBugsFlow(adoClient, NewConnectionAccessor());

        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.ViewBugs, WorkItemId = "123" }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("No linked work items found under #123.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_LinkedItemsFound_ReturnsTableSortedByUrgency()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 1, 2 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => new List<WorkItem>
        {
            new() { Id = 1, Fields = new Dictionary<string, object?> { ["System.Title"] = "Low priority bug", ["System.WorkItemType"] = "Bug", ["System.State"] = "Active", ["Microsoft.VSTS.Common.Priority"] = 4 } },
            new() { Id = 2, Fields = new Dictionary<string, object?> { ["System.Title"] = "Overdue bug", ["System.WorkItemType"] = "Bug", ["System.State"] = "Active", ["Microsoft.VSTS.Scheduling.DueDate"] = DateTime.UtcNow.AddDays(-2).ToString("O") } },
        };

        var flow = new ViewBugsFlow(adoClient, NewConnectionAccessor());
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.ViewBugs, WorkItemId = "42" }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.NotNull(result.TableHeaders);
        Assert.Equal(new[] { "ID", "Title", "Type", "State", "Priority", "Start Date", "Due Date" }, result.TableHeaders);
        Assert.Equal("2", result.TableRows![0][0]);
        Assert.Contains("most urgent first", result.Output);
        Assert.Contains("#42", result.Output);
    }
}
