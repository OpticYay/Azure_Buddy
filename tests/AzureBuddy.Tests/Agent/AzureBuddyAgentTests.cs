using AzureBuddy.Core.Agent;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Core.WorkItemStates;
using AzureBuddy.Data;
using AzureBuddy.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBuddy.Tests.Agent;

public class AzureBuddyAgentTests
{
    private sealed class ScriptedChatClient : IChatCompletionClient
    {
        private readonly Queue<string> _responses;
        public List<ChatHistory> ReceivedHistories { get; } = new();
        public string ProviderName => "Scripted";

        public ScriptedChatClient(params string[] responses) => _responses = new Queue<string>(responses);

        public Task<ChatCompletionResult> CompleteAsync(ChatHistory history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            ReceivedHistories.Add(history);
            var text = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(new ChatCompletionResult { ProviderName = ProviderName, FinishReason = ChatFinishReason.Stop, Text = text });
        }
    }

    private sealed class AlwaysFailingChatClient : IChatCompletionClient
    {
        public string ProviderName => "AlwaysFails";

        public Task<ChatCompletionResult> CompleteAsync(ChatHistory history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) =>
            throw new ChatCompletionProviderException(ProviderName, "simulated provider outage");
    }

    /// <summary>Records every window it was asked to save, so a test can assert Save was (or wasn't)
    /// called without needing a real Redis/in-memory backing store.</summary>
    private sealed class SpyChatHistoryStore : IChatHistoryStore
    {
        public List<ChatSessionWindow> Saved { get; } = new();

        public Task<ChatSessionWindow> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Tests construct the window directly and never call LoadAsync on this spy.");

        public Task SaveAsync(ChatSessionWindow window, CancellationToken cancellationToken = default)
        {
            Saved.Add(window);
            return Task.CompletedTask;
        }

        public Task EvictAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ToolCatalog NewToolCatalog()
    {
        var adoClient = new FakeAdoClient();
        var connectionAccessor = new AdoConnectionContextAccessor
        {
            Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "fake-pat"),
        };
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var stateConfigService = new WorkItemStateConfigService(new AppDbContext(options), new MemoryCache(new MemoryCacheOptions()));
        return new ToolCatalog(new AdoWorkItemToolset(
            adoClient,
            connectionAccessor,
            stateConfigService,
            new AdoIdentityResolver(adoClient),
            new InMemoryPendingAttachmentStore(Options.Create(new AdoOptions())),
            new ChatSessionContextAccessor(),
            new AdoAttachmentService(adoClient, NullLogger<AdoAttachmentService>.Instance)));
    }

    [Fact]
    public async Task RespondAsync_ProviderFailure_DoesNotPersistPartialTurn()
    {
        var chatClient = new AlwaysFailingChatClient();
        var historyStore = new SpyChatHistoryStore();
        var agent = new AzureBuddyAgent(chatClient, historyStore, NewToolCatalog(), NullLogger<AzureBuddyAgent>.Instance);

        var window = new ChatSessionWindow("session-1", messages: Array.Empty<ChatMessage>(), version: "v1");
        window.AddUserTurn(ChatMessage.User("file a bug"));

        var reply = await agent.RespondAsync(window);

        Assert.Equal(string.Empty, reply);
        Assert.Empty(historyStore.Saved);
    }

    [Fact]
    public async Task RespondAsync_RehydratedWindowWithNoSystemMessage_ReAddsSystemPrompt()
    {
        // A window rehydrated from MySQL never contains a System message - ChatMessageRole in the
        // database only has User/Assistant - so this is the only thing that rebuilds it after a miss.
        var chatClient = new ScriptedChatClient("Here you go.");
        var historyStore = new SpyChatHistoryStore();
        var agent = new AzureBuddyAgent(chatClient, historyStore, NewToolCatalog(), NullLogger<AzureBuddyAgent>.Instance);

        var window = new ChatSessionWindow(
            "session-1", new[] { ChatMessage.User("earlier question"), ChatMessage.Assistant("earlier answer") }, version: "v1");
        window.AddUserTurn(ChatMessage.User("a new question"));

        await agent.RespondAsync(window);

        var sentHistory = chatClient.ReceivedHistories.Single();
        Assert.Contains(sentHistory.Messages, m => m.Role == ChatRole.System);
        Assert.Equal(ChatRole.System, sentHistory.Messages[0].Role);
    }

    [Fact]
    public async Task RespondAsync_WindowAlreadyHasSystemMessage_DoesNotAddAnother()
    {
        var chatClient = new ScriptedChatClient("Here you go.");
        var historyStore = new SpyChatHistoryStore();
        var agent = new AzureBuddyAgent(chatClient, historyStore, NewToolCatalog(), NullLogger<AzureBuddyAgent>.Instance);

        var window = new ChatSessionWindow("session-1", new[] { ChatMessage.System("existing prompt") }, version: "v1");
        window.AddUserTurn(ChatMessage.User("a question"));

        await agent.RespondAsync(window);

        var sentHistory = chatClient.ReceivedHistories.Single();
        Assert.Single(sentHistory.Messages, m => m.Role == ChatRole.System);
        Assert.Equal("existing prompt", sentHistory.Messages.First(m => m.Role == ChatRole.System).Content);
    }

    [Fact]
    public async Task RespondAsync_NormalReply_SavesWindow()
    {
        var chatClient = new ScriptedChatClient("Here you go.");
        var historyStore = new SpyChatHistoryStore();
        var agent = new AzureBuddyAgent(chatClient, historyStore, NewToolCatalog(), NullLogger<AzureBuddyAgent>.Instance);

        var window = new ChatSessionWindow("session-1", messages: Array.Empty<ChatMessage>(), version: "v1");
        window.AddUserTurn(ChatMessage.User("a question"));

        var reply = await agent.RespondAsync(window);

        Assert.Equal("Here you go.", reply);
        Assert.Single(historyStore.Saved);
    }

    [Fact]
    public void SystemPrompt_BugDescriptionRule_MatchesSharedTemplate()
    {
        // Guards against the three-way HTML-template duplication described in
        // docs/improvements/04-refactor-and-dedup.md §4.5: CreateBugFlow, this prompt, and
        // AdoAttachmentService must all agree on the bug-description shape.
        Assert.Contains(BugDescriptionTemplate.PromptRule, AzureBuddyAgent.SystemPromptForTests);
    }
}
