using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests.Intent;

public class IntentExtractorTests
{
    private sealed class ScriptedChatClient : IChatCompletionClient
    {
        private readonly Func<ChatCompletionResult> _behavior;
        public string ProviderName => "Scripted";

        public ScriptedChatClient(string rawResponse) => _behavior = () => new ChatCompletionResult
        {
            ProviderName = ProviderName,
            FinishReason = ChatFinishReason.Stop,
            Text = rawResponse
        };

        public ScriptedChatClient(Func<ChatCompletionResult> behavior) => _behavior = behavior;

        public Task<ChatCompletionResult> CompleteAsync(ChatHistory history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default) =>
            Task.FromResult(_behavior());
    }

    private static IntentExtractor NewExtractor(string rawResponse) =>
        new(new ScriptedChatClient(rawResponse), NullLogger<IntentExtractor>.Instance);

    [Theory]
    [InlineData("create_bug", ChatIntent.CreateBug)]
    [InlineData("view_bugs", ChatIntent.ViewBugs)]
    [InlineData("update_item", ChatIntent.UpdateItem)]
    [InlineData("my_items", ChatIntent.MyItems)]
    [InlineData("prioritize_work_items", ChatIntent.PrioritizeWorkItems)]
    [InlineData("other", ChatIntent.Other)]
    public async Task ExtractAsync_ParsesEachKnownIntent(string rawIntent, ChatIntent expected)
    {
        var extractor = NewExtractor($$"""{"intent":"{{rawIntent}}"}""");

        var result = await extractor.ExtractAsync("some message");

        Assert.Equal(expected, result.Intent);
    }

    [Fact]
    public async Task ExtractAsync_UnrecognizedIntentString_FallsBackToOther()
    {
        var extractor = NewExtractor("""{"intent":"something_the_model_invented"}""");

        var result = await extractor.ExtractAsync("some message");

        Assert.Equal(ChatIntent.Other, result.Intent);
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_FallsBackToOther()
    {
        var extractor = NewExtractor("not json at all");

        var result = await extractor.ExtractAsync("some message");

        Assert.Equal(ChatIntent.Other, result.Intent);
    }

    [Fact]
    public async Task ExtractAsync_ModelWrapsJsonInProseOrCodeFences_StillExtractsTheObject()
    {
        var extractor = NewExtractor("""
            Sure, here you go:
            ```json
            {"intent":"my_items","state":"Active"}
            ```
            """);

        var result = await extractor.ExtractAsync("show my active items");

        Assert.Equal(ChatIntent.MyItems, result.Intent);
        Assert.Equal("Active", result.State);
    }

    [Fact]
    public async Task ExtractAsync_ProviderThrows_FallsBackToOtherWithoutPropagating()
    {
        var extractor = new IntentExtractor(
            new ScriptedChatClient(() => throw new ChatCompletionProviderException("Test", "simulated outage")),
            NullLogger<IntentExtractor>.Instance);

        var result = await extractor.ExtractAsync("some message");

        Assert.Equal(ChatIntent.Other, result.Intent);
    }

    [Fact]
    public async Task ExtractAsync_WorkItemIdWithNonDigitCharacters_StripsToDigitsOnly()
    {
        var extractor = NewExtractor("""{"intent":"update_item","work_item_id":"#42","state":"Active"}""");

        var result = await extractor.ExtractAsync("close #42");

        Assert.Equal("42", result.WorkItemId);
    }

    [Fact]
    public async Task ExtractAsync_MissingOptionalFields_DefaultToEmptyRatherThanNull()
    {
        var extractor = NewExtractor("""{"intent":"other"}""");

        var result = await extractor.ExtractAsync("hi");

        Assert.Equal(string.Empty, result.ParentSearchTerm);
        Assert.Equal(string.Empty, result.Title);
        Assert.Empty(result.ReproSteps);
        Assert.False(result.HasAdditionalRequest);
        Assert.False(result.IsUrgent);
    }

    [Fact]
    public async Task ExtractAsync_FullPayload_MapsEveryField()
    {
        const string raw = """
            {
              "intent": "create_bug",
              "parent_search_term": "login story",
              "work_item_id": "",
              "title": "Login fails on submit",
              "repro_steps": ["Open login page", "Enter credentials", "Click submit"],
              "expected_result": "User is logged in",
              "actual_result": "Error 500",
              "evidence": "https://example.com/screenshot.png",
              "environment": "Chrome on Windows",
              "priority": "1",
              "severity": "1 - Critical",
              "area_path": "Proj\\Area",
              "iteration_path": "Proj\\Sprint1",
              "assigned_to": "user@example.com",
              "state": "",
              "comment": "",
              "work_item_type_filter": "",
              "has_additional_request": true,
              "is_urgent": true
            }
            """;
        var extractor = NewExtractor(raw);

        var result = await extractor.ExtractAsync("file an urgent bug");

        Assert.Equal(ChatIntent.CreateBug, result.Intent);
        Assert.Equal("login story", result.ParentSearchTerm);
        Assert.Equal("Login fails on submit", result.Title);
        Assert.Equal(new[] { "Open login page", "Enter credentials", "Click submit" }, result.ReproSteps);
        Assert.Equal("User is logged in", result.ExpectedResult);
        Assert.Equal("Error 500", result.ActualResult);
        Assert.Equal("https://example.com/screenshot.png", result.Evidence);
        Assert.Equal("Chrome on Windows", result.Environment);
        Assert.Equal("1", result.Priority);
        Assert.Equal("1 - Critical", result.Severity);
        Assert.True(result.HasAdditionalRequest);
        Assert.True(result.IsUrgent);
    }
}
