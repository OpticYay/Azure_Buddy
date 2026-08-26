using System.Text.Json;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using AzureBuddy.Core.Llm.Providers.Ollama;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace AzureBuddy.Tests.Llm;

public class OllamaChatClientTests : IDisposable
{
    private readonly WireMockServer _server;

    public OllamaChatClientTests()
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

    private OllamaChatClient NewClient(int timeoutSeconds = 60) => new(
        new HttpClient(),
        new FakeSettingsProvider(new LlmOptions
        {
            Ollama = new OllamaOptions { BaseUrl = _server.Url!, Model = "qwen2.5:14b-instruct", TimeoutSeconds = timeoutSeconds }
        }),
        NullLogger<OllamaChatClient>.Instance);

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
            .Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                message = new { role = "assistant", content = "Hello there!" }
            }));

        var client = NewClient();
        var result = await client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>());

        Assert.Equal("Hello there!", result.Text);
        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);
        Assert.Equal(LlmProviderNames.Ollama, result.ProviderName);
    }

    [Fact]
    public async Task CompleteAsync_NonSuccessStatusCode_ThrowsChatCompletionProviderException()
    {
        _server
            .Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("internal error"));

        var client = NewClient();

        var ex = await Assert.ThrowsAsync<ChatCompletionProviderException>(
            () => client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>()));

        Assert.Equal(LlmProviderNames.Ollama, ex.ProviderName);
    }

    [Fact]
    public async Task CompleteAsync_UnreachableHost_ThrowsChatCompletionProviderExceptionMentioningOllama()
    {
        var client = new OllamaChatClient(
            new HttpClient(),
            new FakeSettingsProvider(new LlmOptions { Ollama = new OllamaOptions { BaseUrl = "http://127.0.0.1:1", TimeoutSeconds = 3 } }),
            NullLogger<OllamaChatClient>.Instance);

        var ex = await Assert.ThrowsAsync<ChatCompletionProviderException>(
            () => client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>()));

        Assert.Contains("Ollama", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_ResponseDelaysPastTimeout_AbortsAtConfiguredTimeout()
    {
        // Directly exercises 01-correctness-and-security.md §1.2, same as the Gemini equivalent test.
        _server
            .Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)).WithBodyAsJson(new
            {
                message = new { role = "assistant", content = "too late" }
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
            .Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                message = new
                {
                    role = "assistant",
                    content = "",
                    tool_calls = new[]
                    {
                        new { function = new { name = "search_work_items", arguments = new { name = "login" } } }
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
    public async Task CompleteAsync_ToolDefinitionsInRequest_SentAsOpenAiStyleFunctions()
    {
        _server
            .Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { message = new { role = "assistant", content = "ok" } }));

        var tools = new[]
        {
            new ToolDefinition { Name = "search_work_items", Description = "search", ParametersSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement }
        };

        var client = NewClient();
        await client.CompleteAsync(NewHistory("hi"), tools);

        var logEntry = Assert.Single(_server.LogEntries);
        Assert.Contains("\"type\":\"function\"", logEntry.RequestMessage!.Body);
        Assert.Contains("search_work_items", logEntry.RequestMessage!.Body);
    }

    [Fact]
    public async Task CompleteAsync_SendsModelAndNumCtxFromOptions()
    {
        _server
            .Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { message = new { role = "assistant", content = "ok" } }));

        var client = NewClient();
        await client.CompleteAsync(NewHistory("hi"), Array.Empty<ToolDefinition>());

        var logEntry = Assert.Single(_server.LogEntries);
        Assert.Contains("qwen2.5:14b-instruct", logEntry.RequestMessage!.Body);
        Assert.Contains("num_ctx", logEntry.RequestMessage!.Body);
    }
}
