using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests.Routing;

public class CreateBugFlowTests
{
    private static AdoConnectionContextAccessor NewConnectionAccessor() => new()
    {
        Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
    };

    private static CreateBugFlow NewFlow(FakeAdoClient adoClient) =>
        new(adoClient, NewConnectionAccessor(), NullLogger<CreateBugFlow>.Instance);

    private static WorkItem Parent(int id, string title, string type = "User Story") =>
        new() { Id = id, Fields = new Dictionary<string, object?> { ["System.Title"] = title, ["System.WorkItemType"] = type } };

    [Fact]
    public async Task ExecuteAsync_NoParentCandidatesFound_FallsThroughToAgent()
    {
        var adoClient = new FakeAdoClient(); // default: QueryWiql returns no ids
        var flow = NewFlow(adoClient);

        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "nonexistent story", Title = "t" }, CancellationToken.None);

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task ExecuteAsync_ExactPhraseFindsNothing_WidensToWordsBeforeGivingUp()
    {
        var adoClient = new FakeAdoClient();
        var calls = new List<string>();
        adoClient.QueryWiqlBehavior = (_, wiql) =>
        {
            calls.Add(wiql);
            if (wiql.Contains("[System.Parent]"))
            {
                return Array.Empty<int>();
            }
            // First search call (exact phrase) finds nothing; the widened, word-based retry does.
            return calls.Count(c => !c.Contains("[System.Parent]")) == 1 ? Array.Empty<int>() : new[] { 5 };
        };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => Parent(id, "SOA report")).ToList();
        adoClient.CreateWorkItemBehavior = (_, _, _) => new WorkItem { Id = 100, Fields = new Dictionary<string, object?> { ["System.Title"] = "[Bug] - t" } };

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "SOA report task", Title = "Login broken" }, CancellationToken.None);

        Assert.True(calls.Count(c => !c.Contains("[System.Parent]")) >= 2);
        Assert.True(result.Handled);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousParentMatch_FallsThroughToAgent()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 1, 2 };
        // Two candidates with equally weak, non-distinguishing overlap against the search term -
        // ParentResolver.Resolve requires the best score to beat the runner-up by >= 0.15 margin.
        adoClient.GetWorkItemsBehavior = (_, ids, _) => new List<WorkItem>
        {
            Parent(1, "Report generation feature"),
            Parent(2, "Report export feature"),
        };

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "report feature", Title = "t" }, CancellationToken.None);

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task ExecuteAsync_LikelyDuplicateBugExists_FallsThroughToAgentInsteadOfCreating()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) => wiql.Contains("[System.Parent]") ? new[] { 50 } : new[] { 1 };
        adoClient.GetWorkItemsBehavior = (_, ids, fields) => fields.Contains(AdoFields.Title) && !fields.Contains(AdoFields.WorkItemType)
            ? ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.Title"] = "[Bug] - Login page crashes on submit" } }).ToList()
            : ids.Select(id => Parent(id, "Login page")).ToList();

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "login page", Title = "Login page crashes on submit" }, CancellationToken.None);

        Assert.False(result.Handled);
        Assert.DoesNotContain(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.CreateWorkItemAsync));
    }

    [Fact]
    public async Task ExecuteAsync_NoLinkedBugs_SkipsDuplicateCheckAndCreatesBug()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) => wiql.Contains("[System.Parent]") ? Array.Empty<int>() : new[] { 1 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => Parent(id, "Login page")).ToList();
        adoClient.CreateWorkItemBehavior = (_, _, _) => new WorkItem { Id = 200, Fields = new Dictionary<string, object?> { ["System.Title"] = "[Bug] - New bug" } };

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "login page", Title = "New bug" }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal(200, result.WorkItemId);
        Assert.Contains(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.CreateWorkItemAsync));
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulCreation_ReturnsConfirmationWithBugAndParentIds()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) => wiql.Contains("[System.Parent]") ? Array.Empty<int>() : new[] { 7 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => Parent(id, "Login page")).ToList();
        adoClient.CreateWorkItemBehavior = (_, _, _) => new WorkItem { Id = 300, Fields = new Dictionary<string, object?> { ["System.Title"] = "[Bug] - Crash on submit" } };

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "login page", Title = "Crash on submit" }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Contains("Bug #300 created", result.Output);
        Assert.Contains("Parent #7", result.Output);
        Assert.Equal(300, result.WorkItemId);
    }

    [Fact]
    public async Task ExecuteAsync_UrgentWithoutExplicitPriority_DefaultsPriorityAndNotesIt()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) => wiql.Contains("[System.Parent]") ? Array.Empty<int>() : new[] { 7 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => Parent(id, "Login page")).ToList();
        AzureBuddy.Core.AzureDevOps.JsonPatchOperation[]? capturedOps = null;
        adoClient.CreateWorkItemBehavior = (_, _, ops) => { capturedOps = ops.ToArray(); return new WorkItem { Id = 300 }; };

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "login page", Title = "Prod is down", IsUrgent = true }, CancellationToken.None);

        Assert.Contains("set Priority to 1", result.Output);
        Assert.Contains(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public async Task ExecuteAsync_CreateWorkItemFails_ReturnsErrorWithoutHallucinatingAnId()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, wiql) => wiql.Contains("[System.Parent]") ? Array.Empty<int>() : new[] { 7 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => Parent(id, "Login page")).ToList();
        adoClient.CreateWorkItemBehavior = (_, _, _) => throw new AdoApiException("503 service unavailable");

        var flow = NewFlow(adoClient);
        var result = await flow.ExecuteAsync(new ExtractedIntent { Intent = ChatIntent.CreateBug, ParentSearchTerm = "login page", Title = "t" }, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Contains("couldn't create the bug", result.Output);
        Assert.Null(result.WorkItemId);
    }
}
