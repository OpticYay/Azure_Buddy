using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Core.WorkItemStates;
using AzureBuddy.Data;
using AzureBuddy.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests.Routing;

public class UpdateItemFlowTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static AdoConnectionContextAccessor NewConnectionAccessor() => new()
    {
        Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
    };

    private static ExtractedIntent StateChangeIntent(string workItemId, string state) => new()
    {
        Intent = ChatIntent.UpdateItem,
        WorkItemId = workItemId,
        State = state,
    };

    [Fact]
    public async Task ExecuteAsync_RequestedStateMatchesConfiguredList_ProceedsWithUpdate()
    {
        var dbContext = NewDbContext();
        dbContext.WorkItemStateConfigurations.Add(new AzureBuddy.Data.Entities.WorkItemStateConfiguration
        {
            WorkItemType = "Bug",
            StateName = "Active",
            DisplayOrder = 0,
            IsEnabled = true,
        });
        await dbContext.SaveChangesAsync();
        var stateConfigService = new WorkItemStateConfigService(dbContext, new MemoryCache(new MemoryCacheOptions()));

        var adoClient = new FakeAdoClient();
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.WorkItemType"] = "Bug" } }).ToList();

        var flow = new UpdateItemFlow(adoClient, NewConnectionAccessor(), stateConfigService, NullLogger<UpdateItemFlow>.Instance);

        var result = await flow.ExecuteAsync(StateChangeIntent("42", "active"), CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Contains("New state: Active.", result.Output);
        Assert.Contains(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.UpdateWorkItemAsync));
    }

    [Fact]
    public async Task ExecuteAsync_RequestedStateNotInConfiguredList_OffersValidStatesInsteadOfUpdating()
    {
        var dbContext = NewDbContext();
        dbContext.WorkItemStateConfigurations.AddRange(
            new AzureBuddy.Data.Entities.WorkItemStateConfiguration { WorkItemType = "Bug", StateName = "New", DisplayOrder = 0, IsEnabled = true },
            new AzureBuddy.Data.Entities.WorkItemStateConfiguration { WorkItemType = "Bug", StateName = "Active", DisplayOrder = 1, IsEnabled = true },
            new AzureBuddy.Data.Entities.WorkItemStateConfiguration { WorkItemType = "Bug", StateName = "Resolved", DisplayOrder = 2, IsEnabled = true },
            new AzureBuddy.Data.Entities.WorkItemStateConfiguration { WorkItemType = "Bug", StateName = "Closed", DisplayOrder = 3, IsEnabled = true });
        await dbContext.SaveChangesAsync();
        var stateConfigService = new WorkItemStateConfigService(dbContext, new MemoryCache(new MemoryCacheOptions()));

        var adoClient = new FakeAdoClient();
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.WorkItemType"] = "Bug" } }).ToList();

        var flow = new UpdateItemFlow(adoClient, NewConnectionAccessor(), stateConfigService, NullLogger<UpdateItemFlow>.Instance);

        var result = await flow.ExecuteAsync(StateChangeIntent("42", "Fixed"), CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Contains("'Fixed' isn't a valid state for a Bug here", result.Output);
        Assert.Contains("New, Active, Resolved, Closed", result.Output);
        Assert.DoesNotContain(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.UpdateWorkItemAsync));
    }

    [Fact]
    public async Task ExecuteAsync_NoConfigurationForType_FallsBackToProceedingWithUpdate()
    {
        // Nothing configured at all for "Feature" - validation has nothing to check against, so the
        // request proceeds and Azure DevOps itself is the only gate, same as before this feature existed.
        var stateConfigService = new WorkItemStateConfigService(NewDbContext(), new MemoryCache(new MemoryCacheOptions()));

        var adoClient = new FakeAdoClient();
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.WorkItemType"] = "Feature" } }).ToList();

        var flow = new UpdateItemFlow(adoClient, NewConnectionAccessor(), stateConfigService, NullLogger<UpdateItemFlow>.Instance);

        var result = await flow.ExecuteAsync(StateChangeIntent("42", "AnythingGoes"), CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Contains(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.UpdateWorkItemAsync));
    }
}
