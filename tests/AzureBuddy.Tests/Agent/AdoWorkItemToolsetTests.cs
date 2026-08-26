using System.Text.Json;
using AzureBuddy.Core.Agent;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.WorkItemStates;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using AzureBuddy.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBuddy.Tests.Agent;

/// <summary>Unit tests for the conversational agent's tool methods against FakeAdoClient - these are
/// characterization tests for the exact duplicated-logic paths flagged in
/// 04-refactor-and-dedup.md (§4.2-§4.6), written before that refactor so it can be verified
/// behavior-preserving against them.</summary>
public class AdoWorkItemToolsetTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AdoConnectionContextAccessor NewConnectionAccessor() => new()
    {
        Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
    };

    private static AdoWorkItemToolset NewToolset(FakeAdoClient adoClient, AppDbContext? dbContext = null) =>
        new(
            adoClient,
            NewConnectionAccessor(),
            new WorkItemStateConfigService(dbContext ?? NewDbContext(), new MemoryCache(new MemoryCacheOptions())),
            new AdoIdentityResolver(adoClient),
            new InMemoryPendingAttachmentStore(Options.Create(new AdoOptions())),
            new ChatSessionContextAccessor(),
            new AdoAttachmentService(adoClient, NullLogger<AdoAttachmentService>.Instance));

    private static JsonElement Args(object obj) => JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    // ---- SearchWorkItemsAsync ----

    [Fact]
    public async Task SearchWorkItemsAsync_NoNameProvided_ReturnsErrorWithoutCallingAdo()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.SearchWorkItemsAsync(Args(new { }), CancellationToken.None);

        Assert.Contains("No search phrase provided", result);
        Assert.Empty(adoClient.Calls);
    }

    [Fact]
    public async Task SearchWorkItemsAsync_ExactPhraseMatches_ReturnsWithoutWideningToWords()
    {
        var adoClient = new FakeAdoClient();
        var calls = new List<string>();
        adoClient.QueryWiqlBehavior = (_, wiql) => { calls.Add(wiql); return new[] { 1 }; };
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.Title"] = "Login page", ["System.WorkItemType"] = "User Story" } }).ToList();

        var toolset = NewToolset(adoClient);
        var result = await toolset.SearchWorkItemsAsync(Args(new { name = "Login page" }), CancellationToken.None);

        Assert.Single(calls);
        Assert.Contains("Login page", result);
    }

    [Fact]
    public async Task SearchWorkItemsAsync_ExactPhraseFindsNothing_WidensToIndividualWords()
    {
        var adoClient = new FakeAdoClient();
        var calls = new List<string>();
        adoClient.QueryWiqlBehavior = (_, wiql) =>
        {
            calls.Add(wiql);
            return calls.Count == 1 ? Array.Empty<int>() : new[] { 5 };
        };
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.Title"] = "Testing of SOA report", ["System.WorkItemType"] = "Task" } }).ToList();

        var toolset = NewToolset(adoClient);
        var result = await toolset.SearchWorkItemsAsync(Args(new { name = "SOA report task" }), CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Contains("SOA report", result);
    }

    [Fact]
    public async Task SearchWorkItemsAsync_NoMatches_ReturnsStructuredNotFoundNotBareEmptyArray()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.SearchWorkItemsAsync(Args(new { name = "nonexistent thing" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("found").GetInt32());
        Assert.Equal("nonexistent thing", doc.RootElement.GetProperty("searched").GetString());
    }

    [Fact]
    public async Task SearchWorkItemsAsync_AdoApiException_ReturnsErrorMentioningAzureDevOps()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => throw new AdoApiException("503 service unavailable");
        var toolset = NewToolset(adoClient);

        var result = await toolset.SearchWorkItemsAsync(Args(new { name = "anything" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("Azure DevOps search request failed", doc.RootElement.GetProperty("error").GetString());
    }

    // ---- CreateLinkedBugAsync ----

    [Fact]
    public async Task CreateLinkedBugAsync_SendsTitleDescriptionAndParentLink()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.CreateWorkItemBehavior = (_, _, ops) => { capturedOps = ops; return new WorkItem { Id = 99, Fields = new Dictionary<string, object?> { ["System.Title"] = "[Bug] - Login fails" } }; };

        var toolset = NewToolset(adoClient);
        var result = await toolset.CreateLinkedBugAsync(Args(new { title = "[Bug] - Login fails", description = "steps", parent_id = "42" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(99, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.Title");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.TCM.ReproSteps");
        Assert.Contains(capturedOps!, op => op.Path == "/relations/-");
    }

    [Fact]
    public async Task CreateLinkedBugAsync_OptionalFieldsOmittedWhenNotProvided()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.CreateWorkItemBehavior = (_, _, ops) => { capturedOps = ops; return new WorkItem { Id = 1 }; };

        var toolset = NewToolset(adoClient);
        await toolset.CreateLinkedBugAsync(Args(new { title = "t", description = "d", parent_id = "1" }), CancellationToken.None);

        Assert.DoesNotContain(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.Common.Priority");
        Assert.DoesNotContain(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.Common.Severity");
    }

    [Fact]
    public async Task CreateLinkedBugAsync_OptionalFieldsIncludedWhenProvided()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.CreateWorkItemBehavior = (_, _, ops) => { capturedOps = ops; return new WorkItem { Id = 1 }; };

        var toolset = NewToolset(adoClient);
        await toolset.CreateLinkedBugAsync(Args(new { title = "t", description = "d", parent_id = "1", priority = "1", severity = "1 - Critical", area_path = "Proj\\Area", iteration_path = "Proj\\Sprint1", assigned_to = "user@example.com" }), CancellationToken.None);

        Assert.Contains(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.Common.Priority");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.Common.Severity");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.AreaPath");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.IterationPath");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.AssignedTo");
    }

    [Fact]
    public async Task CreateLinkedBugAsync_AdoApiException_ReturnsErrorMessage()
    {
        var adoClient = new FakeAdoClient();
        adoClient.CreateWorkItemBehavior = (_, _, _) => throw new AdoApiException("400 bad request");
        var toolset = NewToolset(adoClient);

        var result = await toolset.CreateLinkedBugAsync(Args(new { title = "t", description = "d", parent_id = "1" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("400", doc.RootElement.GetProperty("error").GetString());
    }

    // ---- CreateWorkItemAsync ----

    [Fact]
    public async Task CreateWorkItemAsync_UnsupportedType_ReturnsErrorWithoutCallingAdo()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.CreateWorkItemAsync(Args(new { type = "Bug", title = "t", description = "d" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("Unsupported work item type", doc.RootElement.GetProperty("error").GetString());
        Assert.Empty(adoClient.Calls);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("User Story")]
    [InlineData("Feature")]
    public async Task CreateWorkItemAsync_SupportedType_SendsTitleAndDescription(string type)
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        string? capturedType = null;
        adoClient.CreateWorkItemBehavior = (_, workItemType, ops) =>
        {
            capturedType = workItemType;
            capturedOps = ops;
            return new WorkItem { Id = 99, Fields = new Dictionary<string, object?> { ["System.Title"] = "t" } };
        };

        var toolset = NewToolset(adoClient);
        var result = await toolset.CreateWorkItemAsync(Args(new { type, title = "t", description = "d" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(99, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(type, capturedType);
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.Title");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.Description");
        Assert.DoesNotContain(capturedOps!, op => op.Path == "/relations/-");
    }

    [Fact]
    public async Task CreateWorkItemAsync_ParentIdProvided_AddsParentLink()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.CreateWorkItemBehavior = (_, _, ops) => { capturedOps = ops; return new WorkItem { Id = 1 }; };

        var toolset = NewToolset(adoClient);
        await toolset.CreateWorkItemAsync(Args(new { type = "Task", title = "t", description = "d", parent_id = "42" }), CancellationToken.None);

        Assert.Contains(capturedOps!, op => op.Path == "/relations/-");
    }

    [Fact]
    public async Task CreateWorkItemAsync_OptionalFieldsIncludedWhenProvided()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.CreateWorkItemBehavior = (_, _, ops) => { capturedOps = ops; return new WorkItem { Id = 1 }; };

        var toolset = NewToolset(adoClient);
        await toolset.CreateWorkItemAsync(Args(new { type = "User Story", title = "t", description = "d", priority = "1", area_path = "Proj\\Area", iteration_path = "Proj\\Sprint1", assigned_to = "user@example.com" }), CancellationToken.None);

        Assert.Contains(capturedOps!, op => op.Path == "/fields/Microsoft.VSTS.Common.Priority");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.AreaPath");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.IterationPath");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.AssignedTo");
    }

    [Fact]
    public async Task CreateWorkItemAsync_AdoApiException_ReturnsErrorMessage()
    {
        var adoClient = new FakeAdoClient();
        adoClient.CreateWorkItemBehavior = (_, _, _) => throw new AdoApiException("400 bad request");
        var toolset = NewToolset(adoClient);

        var result = await toolset.CreateWorkItemAsync(Args(new { type = "Feature", title = "t", description = "d" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("400", doc.RootElement.GetProperty("error").GetString());
    }

    // ---- GetLinkedItemsAsync ----

    [Fact]
    public async Task GetLinkedItemsAsync_InvalidParentId_ReturnsErrorWithoutCallingAdo()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.GetLinkedItemsAsync(Args(new { parent_id = "not-a-number" }), CancellationToken.None);

        Assert.Contains("No valid numerical parent_id", result);
        Assert.Empty(adoClient.Calls);
    }

    [Fact]
    public async Task GetLinkedItemsAsync_ValidParentId_ReturnsChildIds()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 10, 11 };
        var toolset = NewToolset(adoClient);

        var result = await toolset.GetLinkedItemsAsync(Args(new { parent_id = "42" }), CancellationToken.None);

        Assert.Equal("[10,11]", result);
    }

    // ---- GetWorkItemDetailsAsync ----

    [Fact]
    public async Task GetWorkItemDetailsAsync_NoIdsFound_ReturnsEmptyArrayWithoutCallingAdo()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.GetWorkItemDetailsAsync(Args(new { ids = "no digits here" }), CancellationToken.None);

        Assert.Equal("[]", result);
        Assert.Empty(adoClient.Calls);
    }

    [Fact]
    public async Task GetWorkItemDetailsAsync_ParsesCommaSeparatedIdsAndIncludesOverdueFlag()
    {
        var adoClient = new FakeAdoClient();
        IEnumerable<int>? capturedIds = null;
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
        {
            capturedIds = ids;
            return ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?>
                {
                    ["System.Title"] = $"Item {id}",
                    ["Microsoft.VSTS.Scheduling.DueDate"] = DateTime.UtcNow.AddDays(-5).ToString("O"),
                },
            }).ToList();
        };

        var toolset = NewToolset(adoClient);
        var result = await toolset.GetWorkItemDetailsAsync(Args(new { ids = "12172,12171" }), CancellationToken.None);

        Assert.Equal(new[] { 12172, 12171 }, capturedIds);
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement[0].GetProperty("overdue").GetBoolean());
    }

    // ---- UpdateWorkItemAsync ----

    [Fact]
    public async Task UpdateWorkItemAsync_InvalidId_ReturnsErrorWithoutCallingAdo()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.UpdateWorkItemAsync(Args(new { id = "abc", state = "Active" }), CancellationToken.None);

        Assert.Contains("No valid numerical id", result);
        Assert.Empty(adoClient.Calls);
    }

    [Fact]
    public async Task UpdateWorkItemAsync_StateNotInConfiguredList_ReturnsValidStatesWithoutUpdating()
    {
        var dbContext = NewDbContext();
        dbContext.WorkItemStateConfigurations.AddRange(
            new WorkItemStateConfiguration { WorkItemType = "Bug", StateName = "New", DisplayOrder = 0, IsEnabled = true },
            new WorkItemStateConfiguration { WorkItemType = "Bug", StateName = "Active", DisplayOrder = 1, IsEnabled = true });
        await dbContext.SaveChangesAsync();

        var adoClient = new FakeAdoClient();
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.WorkItemType"] = "Bug" } }).ToList();

        var toolset = NewToolset(adoClient, dbContext);
        var result = await toolset.UpdateWorkItemAsync(Args(new { id = "1", state = "Fixed" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("isn't a valid state", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.UpdateWorkItemAsync));
    }

    [Fact]
    public async Task UpdateWorkItemAsync_ValidStateAndComment_UpdatesBothFields()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.UpdateWorkItemBehavior = (_, id, ops) => { capturedOps = ops; return new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.State"] = "Resolved" } }; };

        var toolset = NewToolset(adoClient);
        var result = await toolset.UpdateWorkItemAsync(Args(new { id = "42", state = "Resolved", comment = "Fixed it" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.State");
        Assert.Contains(capturedOps!, op => op.Path == "/fields/System.History");
    }

    [Fact]
    public async Task UpdateWorkItemAsync_NoConfigForType_ProceedsWithUpdate()
    {
        var adoClient = new FakeAdoClient();
        adoClient.GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem { Id = id, Fields = new Dictionary<string, object?> { ["System.WorkItemType"] = "Feature" } }).ToList();

        var toolset = NewToolset(adoClient);
        await toolset.UpdateWorkItemAsync(Args(new { id = "42", state = "AnythingGoes" }), CancellationToken.None);

        Assert.Contains(adoClient.Calls, c => c.Method == nameof(FakeAdoClient.UpdateWorkItemAsync));
    }

    [Fact]
    public async Task UpdateWorkItemAsync_AdoApiException_ReturnsErrorMessage()
    {
        var adoClient = new FakeAdoClient();
        adoClient.UpdateWorkItemBehavior = (_, _, _) => throw new AdoApiException("404 not found");
        var toolset = NewToolset(adoClient);

        var result = await toolset.UpdateWorkItemAsync(Args(new { id = "42", comment = "note" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("404", doc.RootElement.GetProperty("error").GetString());
    }

    // ---- GetMyWorkItemsAsync / GetPrioritizedWorkItemsAsync ----

    [Fact]
    public async Task GetMyWorkItemsAsync_NoIds_ReturnsEmptyArray()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.GetMyWorkItemsAsync(Args(new { }), CancellationToken.None);

        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task GetPrioritizedWorkItemsAsync_ReturnsItemsSortedByUrgency()
    {
        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 1, 2 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) => new List<WorkItem>
        {
            new() { Id = 1, Fields = new Dictionary<string, object?> { ["System.Title"] = "Low priority", ["Microsoft.VSTS.Common.Priority"] = 4 } },
            new() { Id = 2, Fields = new Dictionary<string, object?> { ["System.Title"] = "Overdue", ["Microsoft.VSTS.Scheduling.DueDate"] = DateTime.UtcNow.AddDays(-3).ToString("O") } },
        };

        var toolset = NewToolset(adoClient);
        var result = await toolset.GetPrioritizedWorkItemsAsync(Args(new { }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        // The overdue item must be ranked ahead of the merely-low-priority one.
        Assert.Equal(2, doc.RootElement[0].GetProperty("id").GetInt32());
    }

    // ---- AttachEvidenceLinkAsync ----

    [Fact]
    public async Task AttachEvidenceLinkAsync_InvalidId_ReturnsErrorWithoutCallingAdo()
    {
        var adoClient = new FakeAdoClient();
        var toolset = NewToolset(adoClient);

        var result = await toolset.AttachEvidenceLinkAsync(Args(new { id = "xyz", evidence_url = "https://example.com/log.png" }), CancellationToken.None);

        Assert.Contains("No valid numerical id", result);
        Assert.Empty(adoClient.Calls);
    }

    [Fact]
    public async Task AttachEvidenceLinkAsync_ValidId_AttachesLinkAndReturnsId()
    {
        var adoClient = new FakeAdoClient();
        IReadOnlyList<JsonPatchOperation>? capturedOps = null;
        adoClient.UpdateWorkItemBehavior = (_, id, ops) => { capturedOps = ops; return new WorkItem { Id = id }; };

        var toolset = NewToolset(adoClient);
        var result = await toolset.AttachEvidenceLinkAsync(Args(new { id = "42", evidence_url = "https://example.com/log.png" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Contains(capturedOps!, op => op.Path == "/relations/-");
    }

    [Fact]
    public async Task AttachEvidenceLinkAsync_AdoApiException_ReturnsErrorMessage()
    {
        var adoClient = new FakeAdoClient();
        adoClient.UpdateWorkItemBehavior = (_, _, _) => throw new AdoApiException("502 bad gateway");
        var toolset = NewToolset(adoClient);

        var result = await toolset.AttachEvidenceLinkAsync(Args(new { id = "42", evidence_url = "https://example.com/log.png" }), CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Contains("502", doc.RootElement.GetProperty("error").GetString());
    }
}
