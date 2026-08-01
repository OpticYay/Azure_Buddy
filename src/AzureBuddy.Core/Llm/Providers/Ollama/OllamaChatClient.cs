using System.Net.Http.Json;
using System.Text.Json;
using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBuddy.Core.Llm.Providers.Ollama;

/// <summary>
/// Talks to a local Ollama instance's /api/chat endpoint directly (no Semantic Kernel / OllamaSharp
/// dependency, same rationale as GeminiChatClient - avoid experimental package coupling).
/// Ollama's chat API mirrors OpenAI's shape closely enough (messages/tools/tool_calls) that this
/// translation is straightforward.
/// </summary>
public sealed class OllamaChatClient : IChatCompletionClient
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaChatClient> _logger;

    public string ProviderName => "Ollama";

    public OllamaChatClient(HttpClient httpClient, IOptions<LlmOptions> options, ILogger<OllamaChatClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value.Ollama;
        _logger = logger;
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        ChatHistory history,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(history, tools);
        var url = $"{_options.BaseUrl}/api/chat";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(url, requestBody, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ChatCompletionProviderException(ProviderName, "Ollama request failed or timed out (is it running?).", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Ollama returned {Status}: {Body}", response.StatusCode, body);
            throw new ChatCompletionProviderException(ProviderName, $"Ollama returned {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return ParseResponse(payload);
    }

    private object BuildRequestBody(ChatHistory history, IReadOnlyList<ToolDefinition> tools)
    {
        var messages = history.Messages.Select(ToOllamaMessage).ToList();

        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.ParametersSchema
            }
        }).ToList();

        return new
        {
            model = _options.Model,
            messages,
            stream = false,
            tools = toolDefs.Count > 0 ? toolDefs : null,
            options = new { num_ctx = _options.NumCtx }
        };
    }

    private static object ToOllamaMessage(ChatMessage message)
    {
        var role = message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => "user"
        };

        if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role,
                content = message.Content ?? string.Empty,
                tool_calls = message.ToolCalls.Select(tc => new
                {
                    function = new
                    {
                        name = tc.Name,
                        arguments = JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson)
                    }
                }).ToArray()
            };
        }

        return new { role, content = message.Content ?? string.Empty };
    }

    private ChatCompletionResult ParseResponse(JsonElement payload)
    {
        if (!payload.TryGetProperty("message", out var message))
        {
            throw new ChatCompletionProviderException(ProviderName, "Ollama response had no message.");
        }

        var text = message.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;

        var toolCalls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                var function = tc.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var args = function.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}";
                toolCalls.Add(new ToolCall { Id = Guid.NewGuid().ToString("N"), Name = name, ArgumentsJson = args });
            }
        }

        return new ChatCompletionResult
        {
            ProviderName = ProviderName,
            FinishReason = toolCalls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Text = string.IsNullOrEmpty(text) ? null : text,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }
}
