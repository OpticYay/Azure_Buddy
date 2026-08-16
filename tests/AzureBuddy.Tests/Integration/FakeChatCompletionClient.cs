using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Tests.Integration;

/// <summary>Test double for IChatCompletionClient - never touches a real provider. Serves both callers
/// that share this one interface in the real pipeline: IntentExtractor's single-turn classification call
/// (detected by its system prompt's own marker phrase) always gets back an "other" classification so
/// every message falls through to the conversational agent, which then gets ReplyText. Records every
/// history it was called with so a test can assert on what context the agent actually saw - e.g. proving
/// a second replica's call included the first replica's turn (see TwoReplicaChatContinuityTests).</summary>
public sealed class FakeChatCompletionClient : IChatCompletionClient
{
    private const string IntentExtractionSystemPromptMarker = "silent data-extraction step";
    private const string OtherIntentJson = """
        {"intent":"other","parent_search_term":"","work_item_id":"","title":"","repro_steps":[],"expected_result":"","actual_result":"","evidence":"","environment":"","priority":"","severity":"","area_path":"","iteration_path":"","assigned_to":"","state":"","comment":"","work_item_type_filter":"","has_additional_request":false,"is_urgent":false}
        """;

    public string ProviderName => "Fake";
    public string ReplyText { get; set; } = "Sure, here you go.";
    public List<ChatHistory> ReceivedHistories { get; } = new();

    public Task<ChatCompletionResult> CompleteAsync(ChatHistory history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        ReceivedHistories.Add(history);

        var isIntentExtraction = history.Messages.Any(m => m.Content?.Contains(IntentExtractionSystemPromptMarker) == true);
        var text = isIntentExtraction ? OtherIntentJson : ReplyText;

        return Task.FromResult(new ChatCompletionResult { ProviderName = ProviderName, FinishReason = ChatFinishReason.Stop, Text = text });
    }

    public void Reset()
    {
        ReceivedHistories.Clear();
        ReplyText = "Sure, here you go.";
    }
}
