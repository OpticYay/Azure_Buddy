using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBuddy.Api.Controllers;
using AzureBuddy.Core.Auth;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>
/// Proves the bug this whole Redis effort exists to fix: before RedisChatHistoryStore, ChatHistoryStore
/// was a per-process ConcurrentDictionary, so a second replica behind a load balancer had no way to see
/// conversational context a first replica had already built up - the model's context would silently
/// reset depending on which instance handled which request. Two independent CustomWebApplicationFactory
/// instances built with the infra-sharing constructor stand in for two replicas of one deployment:
/// separate hosts, separate DI containers, but the same MySQL database and (via useRedis) the same
/// Redis key prefix - exactly what two real replicas behind one load balancer would share.
/// </summary>
public class TwoReplicaChatContinuityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task SecondTurn_HandledByDifferentReplica_SeesFirstReplicasTurnInContext()
    {
        await using var replicaA = CustomWebApplicationFactory.CreateWithRedis();
        await replicaA.InitializeAsync();
        await using var replicaB = CustomWebApplicationFactory.CreateReplicaOf(replicaA);
        await replicaB.InitializeAsync();

        var email = $"user-{Guid.NewGuid():N}@example.com";
        const string password = "Str0ng!Passw0rd";

        using var registerClient = replicaA.CreateHttpsClient();
        var registerResponse = await registerClient.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(email, password, "Test User"));
        registerResponse.EnsureSuccessStatusCode();
        var tokens = (await registerResponse.Content.ReadFromJsonAsync<AuthTokens>())!;

        using var clientA = replicaA.CreateHttpsClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var clientB = replicaB.CreateHttpsClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        replicaA.ChatClient.ReplyText = "Hi! How can I help?";
        var firstResponse = await clientA.PostAsJsonAsync("/api/chat", new ChatRequest(null, "hello there"));
        firstResponse.EnsureSuccessStatusCode();
        var firstReply = (await firstResponse.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions))!;

        replicaB.ChatClient.ReplyText = "Sure, doing that now.";
        var secondResponse = await clientB.PostAsJsonAsync("/api/chat", new ChatRequest(firstReply.SessionId, "what about now"));
        secondResponse.EnsureSuccessStatusCode();

        // The conversational agent's own completion call (as opposed to IntentExtractor's short
        // classification call - see FakeChatCompletionClient) is the longest history replica B recorded.
        var agentHistory = replicaB.ChatClient.ReceivedHistories.MaxBy(h => h.Messages.Count)!;

        Assert.Contains(agentHistory.Messages, m => m.Content == "hello there");
        Assert.Contains(agentHistory.Messages, m => m.Content == "Hi! How can I help?");
        Assert.Contains(agentHistory.Messages, m => m.Content == "what about now");
    }

    [Fact]
    public async Task SecondTurn_HandledByDifferentReplicaWithoutRedis_StillSeesFirstReplicasTurnViaMySqlFallback()
    {
        // NOT a regression of the original bug: InMemoryChatHistoryStore (see commit 3) always
        // rehydrates from IChatHistorySource/MySQL on a cache miss, which is exactly what happens here -
        // both replicas share the same MySQL database (via the infra-sharing constructor), so replica B
        // still recovers replica A's turn even with no Redis involved. What Redis actually buys over
        // this fallback is avoiding a MySQL round-trip on every single turn and (via RedisChatHistoryStore's
        // CAS) consistent behavior under concurrent writes to the same session - not "conversation
        // continuity" in isolation, which the MySQL fallback already guarantees on its own.
        await using var replicaA = new CustomWebApplicationFactory();
        await replicaA.InitializeAsync();
        await using var replicaB = CustomWebApplicationFactory.CreateReplicaOf(replicaA);
        await replicaB.InitializeAsync();

        var email = $"user-{Guid.NewGuid():N}@example.com";
        const string password = "Str0ng!Passw0rd";

        using var registerClient = replicaA.CreateHttpsClient();
        var registerResponse = await registerClient.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(email, password, "Test User"));
        registerResponse.EnsureSuccessStatusCode();
        var tokens = (await registerResponse.Content.ReadFromJsonAsync<AuthTokens>())!;

        using var clientA = replicaA.CreateHttpsClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var clientB = replicaB.CreateHttpsClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        replicaA.ChatClient.ReplyText = "Hi! How can I help?";
        var firstResponse = await clientA.PostAsJsonAsync("/api/chat", new ChatRequest(null, "hello there"));
        firstResponse.EnsureSuccessStatusCode();
        var firstReply = (await firstResponse.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions))!;

        replicaB.ChatClient.ReplyText = "Sure, doing that now.";
        var secondResponse = await clientB.PostAsJsonAsync("/api/chat", new ChatRequest(firstReply.SessionId, "what about now"));
        secondResponse.EnsureSuccessStatusCode();

        var agentHistory = replicaB.ChatClient.ReceivedHistories.MaxBy(h => h.Messages.Count)!;

        Assert.Contains(agentHistory.Messages, m => m.Content == "hello there");
        Assert.Contains(agentHistory.Messages, m => m.Content == "Hi! How can I help?");
    }
}
