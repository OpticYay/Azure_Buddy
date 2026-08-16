using AzureBuddy.Core.Agent;
using AzureBuddy.Core.Caching;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace AzureBuddy.Tests.Agent;

/// <summary>Runs every test against a real Redis container (see SharedRedisContainer) rather than a
/// fake - the whole point of this store is the CAS transaction semantics StackExchange.Redis's
/// ITransaction/Condition actually enforce server-side, which a hand-rolled fake couldn't verify.</summary>
public class RedisChatHistoryStoreTests : IAsyncLifetime
{
    private sealed class FakeChatHistorySource : IChatHistorySource
    {
        private readonly IReadOnlyList<ChatMessage> _messages;
        public FakeChatHistorySource(IReadOnlyList<ChatMessage> messages) => _messages = messages;

        public Task<IReadOnlyList<ChatMessage>> LoadRecentAsync(string sessionId, int maxMessages, CancellationToken cancellationToken = default) =>
            Task.FromResult(_messages);
    }

    private IConnectionMultiplexer _multiplexer = null!;
    private string _instanceName = null!;

    public async Task InitializeAsync()
    {
        var container = await SharedRedisContainer.GetAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        // A unique prefix per test instance keeps each test's keys isolated on the one shared container,
        // the same isolation SharedMySqlContainer gets from a uniquely-named database per factory.
        _instanceName = $"test:{Guid.NewGuid()}:";
    }

    public Task DisposeAsync()
    {
        _multiplexer.Dispose();
        return Task.CompletedTask;
    }

    private RedisChatHistoryStore NewStore(IChatHistorySource source) =>
        new(_multiplexer, source, Options.Create(new RedisOptions { InstanceName = _instanceName }), NullLogger<RedisChatHistoryStore>.Instance);

    [Fact]
    public async Task LoadAsync_UnknownSession_RehydratesFromSource()
    {
        var seed = new[] { ChatMessage.User("earlier question"), ChatMessage.Assistant("earlier answer") };
        var store = NewStore(new FakeChatHistorySource(seed));

        var window = await store.LoadAsync(Guid.NewGuid().ToString());

        Assert.Equal(2, window.Messages.Count);
        Assert.Equal(string.Empty, window.Version);
    }

    [Fact]
    public async Task SaveAsync_ThenLoad_RoundTripsMessagesAndAssignsNewVersion()
    {
        var store = NewStore(new FakeChatHistorySource(Array.Empty<ChatMessage>()));
        var sessionId = Guid.NewGuid().ToString();

        var window = await store.LoadAsync(sessionId);
        window.Add(ChatMessage.User("hello"));
        await store.SaveAsync(window);

        var reloaded = await store.LoadAsync(sessionId);

        Assert.Single(reloaded.Messages);
        Assert.Equal("hello", reloaded.Messages[0].Content);
        Assert.NotEqual(string.Empty, reloaded.Version);
    }

    [Fact]
    public async Task EvictAsync_RemovesSession_SoNextLoadRehydratesAgain()
    {
        var source = new FakeChatHistorySource(Array.Empty<ChatMessage>());
        var store = NewStore(source);
        var sessionId = Guid.NewGuid().ToString();

        var window = await store.LoadAsync(sessionId);
        window.Add(ChatMessage.User("hello"));
        await store.SaveAsync(window);

        await store.EvictAsync(sessionId);
        var reloaded = await store.LoadAsync(sessionId);

        Assert.Empty(reloaded.Messages);
        Assert.Equal(string.Empty, reloaded.Version);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentWriters_RebasesLoserOnTopOfWinnerInsteadOfLosingEitherTurn()
    {
        var store = NewStore(new FakeChatHistorySource(Array.Empty<ChatMessage>()));
        var sessionId = Guid.NewGuid().ToString();

        // Two windows for the same never-before-cached session, both loaded before either has saved -
        // both therefore carry Version == string.Empty and will both try to win the same
        // Condition.HashNotExists CAS.
        var windowA = await store.LoadAsync(sessionId);
        windowA.Add(ChatMessage.User("first message"));
        var windowB = await store.LoadAsync(sessionId);
        windowB.Add(ChatMessage.User("second message"));

        await store.SaveAsync(windowA);
        await store.SaveAsync(windowB);

        var reloaded = await store.LoadAsync(sessionId);

        Assert.Equal(2, reloaded.Messages.Count);
        Assert.Equal("first message", reloaded.Messages[0].Content);
        Assert.Equal("second message", reloaded.Messages[1].Content);
    }

    [Fact]
    public async Task LoadAsync_VersionFieldPresentButDataMissing_PreservesVersionForNextSave()
    {
        var sessionId = Guid.NewGuid().ToString();
        var db = _multiplexer.GetDatabase();
        var key = $"{_instanceName}chat:{sessionId}";
        await db.HashSetAsync(key, new[] { new HashEntry("version", "stale-version") });
        var seed = new[] { ChatMessage.User("rehydrated") };
        var store = NewStore(new FakeChatHistorySource(seed));

        var window = await store.LoadAsync(sessionId);

        Assert.Equal("stale-version", window.Version);
        Assert.Single(window.Messages);

        // Proves the preserved version is actually usable for CAS, not just returned for show.
        window.Add(ChatMessage.User("new turn"));
        await store.SaveAsync(window);
        var reloaded = await store.LoadAsync(sessionId);
        Assert.Equal(2, reloaded.Messages.Count);
    }
}
