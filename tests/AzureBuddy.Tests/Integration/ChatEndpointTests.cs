using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBuddy.Api.Controllers;
using AzureBuddy.Core.Chat;
using AzureBuddy.Data.Entities;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of POST /chat, the flagship live-chat endpoint, using
/// CustomWebApplicationFactory + FakeChatCompletionClient (which always classifies intent as "other",
/// so every message here goes through the conversational agent path rather than a deterministic flow -
/// see FakeChatCompletionClient's own doc comment). Covers session creation as a side effect, message
/// persistence, and the NotFound/IDOR paths already proven for /api/chats in ChatsEndpointsTests.</summary>
public class ChatEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public ChatEndpointTests(CustomWebApplicationFactory factory) : base(factory)
    {
        Factory.ChatClient.Reset();
    }

    [Fact]
    public async Task PostAsync_NullSessionId_CreatesASessionAsASideEffect()
    {
        using var client = await CreateAuthenticatedClientAsync();
        Factory.ChatClient.ReplyText = "Hi there!";

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(null, "hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);
        Assert.NotEqual(Guid.Empty, body!.SessionId);
        Assert.Equal("Hi there!", body.Reply);
    }

    [Fact]
    public async Task PostAsync_NullSessionId_PersistsBothTheUserMessageAndTheReply()
    {
        using var client = await CreateAuthenticatedClientAsync();
        Factory.ChatClient.ReplyText = "Sure, here you go.";

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(null, "what are my open items"));
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);

        var detail = await client.GetFromJsonAsync<ChatSessionDetail>($"/api/chats/{body!.SessionId}", JsonOptions);
        Assert.Equal(2, detail!.Messages.Count);
        Assert.Equal(ChatMessageRole.User, detail.Messages[0].Role);
        Assert.Equal("what are my open items", detail.Messages[0].Content);
        Assert.Equal(ChatMessageRole.Assistant, detail.Messages[1].Role);
        Assert.Equal("Sure, here you go.", detail.Messages[1].Content);
    }

    [Fact]
    public async Task PostAsync_ExistingSessionId_AppendsToTheSameSessionRatherThanCreatingANewOne()
    {
        using var client = await CreateAuthenticatedClientAsync();
        Factory.ChatClient.ReplyText = "First reply";
        var first = await (await client.PostAsJsonAsync("/api/chat", new ChatRequest(null, "first message"))).Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);

        Factory.ChatClient.ReplyText = "Second reply";
        var second = await (await client.PostAsJsonAsync("/api/chat", new ChatRequest(first!.SessionId, "second message"))).Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);

        Assert.Equal(first.SessionId, second!.SessionId);
        var detail = await client.GetFromJsonAsync<ChatSessionDetail>($"/api/chats/{first.SessionId}", JsonOptions);
        Assert.Equal(4, detail!.Messages.Count);
    }

    [Fact]
    public async Task PostAsync_UnknownSessionId_ReturnsNotFound()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(Guid.NewGuid(), "hello"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostAsync_AnotherUsersSessionId_ReturnsNotFoundRatherThanLeakingItsExistence()
    {
        using var clientA = await CreateAuthenticatedClientAsync();
        Factory.ChatClient.ReplyText = "A's reply";
        var sessionA = await (await clientA.PostAsJsonAsync("/api/chat", new ChatRequest(null, "A's message"))).Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);

        using var clientB = await CreateAuthenticatedClientAsync();
        var response = await clientB.PostAsJsonAsync("/api/chat", new ChatRequest(sessionA!.SessionId, "injected by B"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostAsync_EmptyMessage_ReturnsBadRequestFromModelValidation()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(null, ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAsync_Unauthenticated_ReturnsUnauthorized()
    {
        using var client = Factory.CreateHttpsClient();

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(null, "hello"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostAsync_AgentReply_TaggedAsTextType()
    {
        // FakeChatCompletionClient's conversational-agent path never returns tool calls, so every reply
        // through it is a plain Text-typed message - see IntentRouter's comment on why the agent never
        // produces Table/Confirmation types itself.
        using var client = await CreateAuthenticatedClientAsync();
        Factory.ChatClient.ReplyText = "Just a plain reply.";

        var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest(null, "hello"));
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions);

        Assert.Equal(ChatMessageType.Text, body!.Type);
        Assert.Null(body.Table);
    }
}
