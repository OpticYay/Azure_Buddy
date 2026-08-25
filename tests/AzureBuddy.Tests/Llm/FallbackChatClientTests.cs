using AzureBuddy.Core.Llm;
using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureBuddy.Tests.Llm;

public class FallbackChatClientTests
{
    private sealed class FakeChatClient : IChatCompletionClient
    {
        private readonly Func<ChatCompletionResult> _behavior;
        public string ProviderName { get; }
        public int CallCount { get; private set; }

        public FakeChatClient(string providerName, Func<ChatCompletionResult> behavior)
        {
            ProviderName = providerName;
            _behavior = behavior;
        }

        public Task<ChatCompletionResult> CompleteAsync(ChatHistory history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_behavior());
        }
    }

    private static ChatCompletionResult Success(string providerName) => new()
    {
        ProviderName = providerName,
        FinishReason = ChatFinishReason.Stop,
        Text = $"response from {providerName}"
    };

    private static FakeChatClient Failing(string providerName) => new(providerName, () =>
        throw new ChatCompletionProviderException(providerName, $"{providerName} is down"));

    [Fact]
    public void Constructor_EmptyProviderList_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FallbackChatClient(Array.Empty<IChatCompletionClient>(), NullLogger<FallbackChatClient>.Instance));
    }

    [Fact]
    public async Task CompleteAsync_PrimarySucceeds_NeverCallsFallback()
    {
        var primary = new FakeChatClient("Primary", () => Success("Primary"));
        var fallback = new FakeChatClient("Fallback", () => Success("Fallback"));

        var client = new FallbackChatClient(new IChatCompletionClient[] { primary, fallback }, NullLogger<FallbackChatClient>.Instance);
        var result = await client.CompleteAsync(new ChatHistory(), Array.Empty<ToolDefinition>());

        Assert.Equal("Primary", result.ProviderName);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_PrimaryFails_FallsThroughToNextProvider()
    {
        var primary = Failing("Primary");
        var fallback = new FakeChatClient("Fallback", () => Success("Fallback"));

        var client = new FallbackChatClient(new IChatCompletionClient[] { primary, fallback }, NullLogger<FallbackChatClient>.Instance);
        var result = await client.CompleteAsync(new ChatHistory(), Array.Empty<ToolDefinition>());

        Assert.Equal("Fallback", result.ProviderName);
    }

    [Fact]
    public async Task CompleteAsync_AllProvidersFail_ThrowsAggregatingException()
    {
        var client = new FallbackChatClient(
            new IChatCompletionClient[] { Failing("Primary"), Failing("Secondary") },
            NullLogger<FallbackChatClient>.Instance);

        var ex = await Assert.ThrowsAsync<ChatCompletionProviderException>(
            () => client.CompleteAsync(new ChatHistory(), Array.Empty<ToolDefinition>()));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task CompleteAsync_SingleProviderTransientFailureThenSuccess_Recovers()
    {
        var attempts = 0;
        var provider = new FakeChatClient("Only", () =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new ChatCompletionProviderException("Only", "transient blip");
            }

            return Success("Only");
        });

        var client = new FallbackChatClient(new IChatCompletionClient[] { provider }, NullLogger<FallbackChatClient>.Instance);
        var result = await client.CompleteAsync(new ChatHistory(), Array.Empty<ToolDefinition>());

        Assert.Equal("Only", result.ProviderName);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CompleteAsync_TriesProvidersInConfiguredOrder()
    {
        var callOrder = new List<string>();
        var first = new FakeChatClient("First", () => { callOrder.Add("First"); throw new ChatCompletionProviderException("First", "down"); });
        var second = new FakeChatClient("Second", () => { callOrder.Add("Second"); return Success("Second"); });

        var client = new FallbackChatClient(new IChatCompletionClient[] { first, second }, NullLogger<FallbackChatClient>.Instance);
        await client.CompleteAsync(new ChatHistory(), Array.Empty<ToolDefinition>());

        Assert.Equal(new[] { "First", "First", "Second" }, callOrder);
    }
}
