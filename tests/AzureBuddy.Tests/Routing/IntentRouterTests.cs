using AzureBuddy.Core.Agent;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Core.Routing;
using AzureBuddy.Core.Routing.Flows;
using AzureBuddy.Core.WorkItemStates;
using AzureBuddy.Data;
using AzureBuddy.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests.Routing;

/// <summary>
/// Covers a real bug reported from actual use: a deterministic flow (MyItemsFlow, ViewBugsFlow, ...)
/// answers a turn without ever going through AzureBuddyAgent, so the agent's own ChatHistoryStore never
/// learned what was shown. A later turn that falls through to the agent ("summarize these bugs") then
/// had no idea what "these" referred to - the agent's memory was missing everything the deterministic
/// flows had handled. IntentRouter.RouteAsync now records every deterministic-flow turn into that same
/// history, keyed by the same sessionId, so the agent's memory matches the full conversation regardless
/// of which turns it personally handled.
/// </summary>
public class IntentRouterTests
{
    /// <summary>Returns a scripted response per call (round-robins the last one once exhausted) and
    /// records every ChatHistory it was called with, so a test can assert on what the model actually saw.</summary>
    private sealed class ScriptedChatClient : IChatCompletionClient
    {
        private readonly Queue<string> _responses;
        public List<ChatHistory> ReceivedHistories { get; } = new();
        public string ProviderName => "Scripted";

        public ScriptedChatClient(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public Task<ChatCompletionResult> CompleteAsync(ChatHistory history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            ReceivedHistories.Add(history);
            var text = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(new ChatCompletionResult
            {
                ProviderName = ProviderName,
                FinishReason = ChatFinishReason.Stop,
                Text = text,
            });
        }
    }

    private const string MyItemsExtraction =
        """{"intent":"my_items","parent_search_term":"","work_item_id":"","title":"","repro_steps":[],"expected_result":"","actual_result":"","evidence":"","environment":"","priority":"","severity":"","area_path":"","iteration_path":"","assigned_to":"","state":"","comment":""}""";

    private const string OtherExtraction =
        """{"intent":"other","parent_search_term":"","work_item_id":"","title":"","repro_steps":[],"expected_result":"","actual_result":"","evidence":"","environment":"","priority":"","severity":"","area_path":"","iteration_path":"","assigned_to":"","state":"","comment":""}""";

    /// <summary>Fresh in-memory-backed WorkItemStateConfigService per test - AdoWorkItemToolset needs
    /// one to construct, but none of these tests exercise state-update validation, so an empty table
    /// (no rows configured for any type) is exactly the "nothing to validate against" case.</summary>
    private static WorkItemStateConfigService NewStateConfigService()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WorkItemStateConfigService(new AppDbContext(options));
    }

    private const string MyItemsWithAdditionalRequestExtraction =
        """{"intent":"my_items","parent_search_term":"","work_item_id":"","title":"","repro_steps":[],"expected_result":"","actual_result":"","evidence":"","environment":"","priority":"","severity":"","area_path":"","iteration_path":"","assigned_to":"","state":"","comment":"","has_additional_request":true}""";

    [Fact]
    public async Task RouteAsync_ExtractionHasAdditionalRequest_SkipsDeterministicFlowAndUsesAgent()
    {
        // Even though the extraction matched "my_items" cleanly, has_additional_request:true signals the
        // message also asked for something else - MyItemsFlow can only ever answer the one thing, so this
        // must fall straight through to the full agent instead of silently dropping the second request.
        var extractorClient = new ScriptedChatClient(MyItemsWithAdditionalRequestExtraction);
        var intentExtractor = new IntentExtractor(extractorClient, NullLogger<IntentExtractor>.Instance);

        var adoClient = new FakeAdoClient();
        var connectionAccessor = new AdoConnectionContextAccessor
        {
            Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
        };
        // MyItemsFlow would normally handle this - passing a real instance proves it's genuinely never
        // invoked (its default FakeAdoClient behavior would answer "No matching work items found").
        var myItemsFlow = new MyItemsFlow(adoClient, connectionAccessor);

        var historyStore = new ChatHistoryStore();
        var agentClient = new ScriptedChatClient("Handled both parts of your request.");
        var toolCatalog = new ToolCatalog(new AdoWorkItemToolset(adoClient, connectionAccessor, NewStateConfigService()));
        var agent = new AzureBuddyAgent(agentClient, historyStore, toolCatalog, NullLogger<AzureBuddyAgent>.Instance);

        var router = new IntentRouter(intentExtractor, null!, null!, null!, myItemsFlow, null!, agent, historyStore);

        var sessionId = Guid.NewGuid().ToString();
        var reply = await router.RouteAsync(sessionId, "show my open items and also file a bug for the login crash", CancellationToken.None);

        Assert.Equal("Handled both parts of your request.", reply.Text);
        Assert.Single(agentClient.ReceivedHistories);
    }

    [Fact]
    public async Task RouteAsync_AgentTurnAfterDeterministicFlow_SeesThatFlowsReplyInItsHistory()
    {
        var extractorClient = new ScriptedChatClient(MyItemsExtraction, OtherExtraction);
        var intentExtractor = new IntentExtractor(extractorClient, NullLogger<IntentExtractor>.Instance);

        var adoClient = new FakeAdoClient();
        adoClient.QueryWiqlBehavior = (_, _) => new[] { 101 };
        adoClient.GetWorkItemsBehavior = (_, ids, _) =>
            ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?>
                {
                    ["System.Title"] = "Sample bug title",
                    ["System.WorkItemType"] = "Bug",
                    ["System.State"] = "Active",
                },
            }).ToList();

        var connectionAccessor = new AdoConnectionContextAccessor
        {
            Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
        };
        var myItemsFlow = new MyItemsFlow(adoClient, connectionAccessor);

        var historyStore = new ChatHistoryStore();
        var agentClient = new ScriptedChatClient("Here is your summary.");
        var toolCatalog = new ToolCatalog(new AdoWorkItemToolset(adoClient, connectionAccessor, NewStateConfigService()));
        var agent = new AzureBuddyAgent(agentClient, historyStore, toolCatalog, NullLogger<AzureBuddyAgent>.Instance);

        // CreateBugFlow/ViewBugsFlow/UpdateItemFlow are never reached (both turns below classify as
        // my_items then other), so passing null is safe - IntentRouter's switch never touches them.
        var router = new IntentRouter(
            intentExtractor,
            createBugFlow: null!,
            viewBugsFlow: null!,
            updateItemFlow: null!,
            myItemsFlow,
            getPrioritizedWorkItemsFlow: null!,
            agent,
            historyStore);

        var sessionId = Guid.NewGuid().ToString();
        await router.RouteAsync(sessionId, "show my open work items", CancellationToken.None);
        await router.RouteAsync(sessionId, "summarize these bugs", CancellationToken.None);

        var historySeenByAgent = agentClient.ReceivedHistories.Single();
        Assert.Contains(historySeenByAgent.Messages, m => m.Role == ChatRole.User && m.Content == "show my open work items");
        Assert.Contains(historySeenByAgent.Messages, m => m.Role == ChatRole.Assistant && m.Content != null && m.Content.Contains("Sample bug title"));
    }

    [Fact]
    public async Task RouteAsync_DeterministicFlowNoResults_StillRecordsTurnInHistory()
    {
        var extractorClient = new ScriptedChatClient(MyItemsExtraction);
        var intentExtractor = new IntentExtractor(extractorClient, NullLogger<IntentExtractor>.Instance);

        var adoClient = new FakeAdoClient(); // default QueryWiqlBehavior returns no ids
        var connectionAccessor = new AdoConnectionContextAccessor
        {
            Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
        };
        var myItemsFlow = new MyItemsFlow(adoClient, connectionAccessor);
        var historyStore = new ChatHistoryStore();
        var agentClient = new ScriptedChatClient("unused");
        var toolCatalog = new ToolCatalog(new AdoWorkItemToolset(adoClient, connectionAccessor, NewStateConfigService()));
        var agent = new AzureBuddyAgent(agentClient, historyStore, toolCatalog, NullLogger<AzureBuddyAgent>.Instance);

        var router = new IntentRouter(intentExtractor, null!, null!, null!, myItemsFlow, null!, agent, historyStore);

        var sessionId = Guid.NewGuid().ToString();
        var reply = await router.RouteAsync(sessionId, "show my open work items", CancellationToken.None);

        Assert.Equal("No matching work items found assigned to you.", reply.Text);
        Assert.Contains(
            historyStore.GetOrCreate(sessionId).Messages,
            m => m.Role == ChatRole.Assistant && m.Content == "No matching work items found assigned to you.");
    }
}
