using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Llm.Models;
using Xunit;

namespace AzureBuddy.Tests.Agent;

public class InMemoryChatHistoryStoreTests
{
    private sealed class FakeChatHistorySource : IChatHistorySource
    {
        private readonly IReadOnlyList<ChatMessage> _messages;
        public int CallCount { get; private set; }

        public FakeChatHistorySource(IReadOnlyList<ChatMessage> messages) => _messages = messages;

        public Task<IReadOnlyList<ChatMessage>> LoadRecentAsync(string sessionId, int maxMessages, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_messages);
        }
    }

    [Fact]
    public async Task LoadAsync_UnknownSession_RehydratesFromSource()
    {
        var seedMessages = new[] { ChatMessage.User("earlier question"), ChatMessage.Assistant("earlier answer") };
        var source = new FakeChatHistorySource(seedMessages);
        var store = new InMemoryChatHistoryStore(new InMemoryChatWindowCache(), source);

        var window = await store.LoadAsync(Guid.NewGuid().ToString());

        Assert.Equal(2, window.Messages.Count);
        Assert.Equal("earlier question", window.Messages[0].Content);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task LoadAsync_AfterSave_DoesNotRehydrateAgain()
    {
        var source = new FakeChatHistorySource(Array.Empty<ChatMessage>());
        var cache = new InMemoryChatWindowCache();
        var store = new InMemoryChatHistoryStore(cache, source);
        var sessionId = Guid.NewGuid().ToString();

        var window = await store.LoadAsync(sessionId);
        window.Add(ChatMessage.User("hello"));
        await store.SaveAsync(window);

        var reloaded = await store.LoadAsync(sessionId);

        Assert.Single(reloaded.Messages);
        Assert.Equal("hello", reloaded.Messages[0].Content);
        // Only the first LoadAsync (the actual miss) should have gone to the source - the second one
        // must be served from the cache written by SaveAsync.
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task EvictAsync_RemovesSession_SoNextLoadRehydratesAgain()
    {
        var source = new FakeChatHistorySource(Array.Empty<ChatMessage>());
        var cache = new InMemoryChatWindowCache();
        var store = new InMemoryChatHistoryStore(cache, source);
        var sessionId = Guid.NewGuid().ToString();

        var window = await store.LoadAsync(sessionId);
        window.Add(ChatMessage.User("hello"));
        await store.SaveAsync(window);

        await store.EvictAsync(sessionId);
        await store.LoadAsync(sessionId);

        Assert.Equal(2, source.CallCount);
    }
}
