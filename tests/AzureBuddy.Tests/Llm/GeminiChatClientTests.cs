using System.Text.Json;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Core.Llm.Providers.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace AzureBuddy.Tests.Llm;

/// <summary>WireMock.Net-backed tests for GeminiChatClient, mirroring the pattern already used for
/// AdoClient (AzureDevOps/AdoClientTests.cs) - a real HTTP server rather than a mocked HttpMessageHandler,
/// so request construction and response parsing are both exercised end-to-end.</summary>
public class GeminiChatClientTests : IDisposable
{
    private readonly WireMockServer _server;

    public GeminiChatClientTests()
    {
        _server = WireMockServer.Start();
    }

    public void Dispose() => _server.Stop();

    private sealed class FakeSettingsProvider : ILlmSettingsProvider
    {
        public LlmOptions Current { get; private set; }
        public FakeSettingsProvider(LlmOptions options) => Current = options;
        public void Refresh(LlmOptions updated) => Current = updated;
    }

    private GeminiChatClient NewClient(int timeoutSeconds = 30) => new(
        new HttpClient(),
        new FakeSettingsProvider(new LlmOptions
        {
            Gemini = new GeminiOptions { ApiKey = "test-key", Model = "gemini-1.5-flash", BaseUrl = _server.Url!, TimeoutSeconds = timeoutSeconds }
        }),
        NullLogger<GeminiChatClient>.Instance);

    private static ChatHistory NewHistory(string userMessage)
    {
        var history = new ChatHistory();
        history.Add(ChatMessage.User(userMessage));
        return history;
    }

    [Fact]
    public async Task CompleteAsync_SuccessfulTextResponse_ParsesTheAnswer()
    {
        _server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                candidates = new[]
                {
                    new { content = new { parts = new[] { new { text = "Hello there!" } } } }
                }
            }));

        var client = NewClient();
        var result = await client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>());

        Assert.Equal("Hello there!", result.Text);
        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);
        Assert.Equal(LlmProviderNames.Gemini, result.ProviderName);
    }

    [Fact]
    public async Task CompleteAsync_FiltersOutThoughtPartsFromTheFinalText()
    {
        // Confirmed real observed bug this fixes: Gemini's "thinking" models return internal reasoning
        // as its own text part, marked "thought": true, ahead of the actual answer part - without the
        // filter, the reasoning gets silently concatenated onto the front of the reply.
        _server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new object[]
                            {
                                new { text = "The user said 'hi'... I should respond politely.", thought = true },
                                new { text = "Hello! How can I help?" }
                            }
                        }
                    }
                }
            }));

        var client = NewClient();
        var result = await client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>());

        Assert.Equal("Hello! How can I help?", result.Text);
        Assert.DoesNotContain("respond politely", result.Text);
    }

    [Fact]
    public async Task CompleteAsync_NonSuccessStatusCode_ThrowsChatCompletionProviderException()
    {
        _server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("internal error"));

        var client = NewClient();

        var ex = await Assert.ThrowsAsync<ChatCompletionProviderException>(
            () => client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>()));

        Assert.Equal(LlmProviderNames.Gemini, ex.ProviderName);
    }

    [Fact]
    public async Task CompleteAsync_ResponseDelaysPastTimeout_AbortsAtConfiguredTimeout()
    {
        // Directly exercises 01-correctness-and-security.md §1.2: a stub that accepts the request
        // promptly but delays the response body past TimeoutSeconds. The call must abort around the
        // configured timeout rather than hanging on HttpClient's own (much longer, or absent) default.
        _server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)).WithBodyAsJson(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = "too late" } } } } }
            }));

        var client = NewClient(timeoutSeconds: 1);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<ChatCompletionProviderException>(
            () => client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>()));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Expected the call to abort near the 1s timeout, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task CompleteAsync_ToolCallResponse_RoundTripsNameAndArguments()
    {
        _server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new object[]
                            {
                                new { functionCall = new { name = "search_work_items", args = new { name = "login" } } }
                            }
                        }
                    }
                }
            }));

        var tools = new[]
        {
            new ToolDefinition { Name = "search_work_items", Description = "search", ParametersSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement }
        };

        var client = NewClient();
        var result = await client.CompleteAsync(NewHistory("find the login item"), tools);

        Assert.Equal(ChatFinishReason.ToolCalls, result.FinishReason);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("search_work_items", call.Name);
        using var argsDoc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("login", argsDoc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CompleteAsync_ToolDefinitionsInRequest_SentAsFunctionDeclarations()
    {
        _server
            .Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = "ok" } } } } }
            }));

        var tools = new[]
        {
            new ToolDefinition { Name = "search_work_items", Description = "search", ParametersSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement }
        };

        var client = NewClient();
        await client.CompleteAsync(NewHistory("hi"), tools);

        var logEntry = Assert.Single(_server.LogEntries);
        Assert.Contains("functionDeclarations", logEntry.RequestMessage!.Body);
        Assert.Contains("search_work_items", logEntry.RequestMessage!.Body);
    }
}
