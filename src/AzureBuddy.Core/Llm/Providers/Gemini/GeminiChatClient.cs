using System.Net.Http.Json;
using System.Text.Json;
using AzureBuddy.Core.Llm.Models;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Llm.Providers.Gemini;

/// <summary>
/// Talks to the Gemini "generateContent" REST API directly (no Semantic Kernel dependency).
/// Translates the provider-agnostic ChatHistory/ToolDefinition into Gemini's "contents"/"tools" shape
/// and translates the response back into ChatCompletionResult.
/// </summary>
public sealed class GeminiChatClient : IChatCompletionClient
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiChatClient> _logger;

    public string ProviderName => LlmProviderNames.Gemini;

    // Reads ILlmSettingsProvider.Current at CONSTRUCTION time, not per-call - safe because
    // AddHttpClient<GeminiChatClient>() registers this class as transient, and its DI registration
    // (see LlmServiceCollectionExtensions) resolves a fresh instance for every chat completion, so
    // "read once, at construction" and "read fresh every request" end up meaning the same thing here.
    public GeminiChatClient(HttpClient httpClient, ILlmSettingsProvider settingsProvider, ILogger<GeminiChatClient> logger)
    {
        _httpClient = httpClient;
        _options = settingsProvider.Current.Gemini;
        _logger = logger;
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        ChatHistory history,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(history, tools);
        var url = $"{_options.BaseUrl}/models/{_options.Model}:generateContent?key={_options.ApiKey}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(url, requestBody, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ChatCompletionProviderException(ProviderName, "Gemini request failed or timed out.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Gemini returned {Status}: {Body}", response.StatusCode, body);
            throw new ChatCompletionProviderException(ProviderName, $"Gemini returned {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        return ParseResponse(payload);
    }

    private static object BuildRequestBody(ChatHistory history, IReadOnlyList<ToolDefinition> tools)
    {
        var systemInstructionText = string.Join(
            "\n\n",
            history.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));

        var contents = history.Messages
            .Where(m => m.Role != ChatRole.System)
            .Select(ToGeminiContent)
            .ToList();

        var functionDeclarations = tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.ParametersSchema
        }).ToList();

        return new
        {
            systemInstruction = string.IsNullOrEmpty(systemInstructionText)
                ? null
                : new { parts = new[] { new { text = systemInstructionText } } },
            contents,
            tools = functionDeclarations.Count > 0
                ? new[] { new { functionDeclarations } }
                : null
        };
    }

    private static object ToGeminiContent(ChatMessage message)
    {
        var role = message.Role switch
        {
            ChatRole.User => "user",
            ChatRole.Assistant => "model",
            // Gemini's generateContent endpoint rejects role "function" for tool-result turns
            // ("Role 'function' is not supported... use USER, MODEL...") - the functionResponse
            // wrapper inside the part is what marks it as a tool result, so the outer role just
            // needs to be "user".
            ChatRole.Tool => "user",
            _ => "user"
        };

        if (message.Role == ChatRole.Tool)
        {
            return new
            {
                role,
                parts = new object[]
                {
                    new
                    {
                        functionResponse = new
                        {
                            name = message.ToolName,
                            response = new { content = message.Content }
                        }
                    }
                }
            };
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role,
                // thoughtSignature must ride alongside the functionCall part it was issued for - Gemini
                // 3 rejects a replayed functionCall that's missing it (see GeminiThoughtSignature). Only
                // set the property at all when we have a value, since Gemini also rejects the key with a
                // null/empty signature.
                parts = message.ToolCalls.Select(tc => tc.GeminiThoughtSignature is { Length: > 0 } sig
                    ? (object)new
                    {
                        functionCall = new
                        {
                            name = tc.Name,
                            args = JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson)
                        },
                        thoughtSignature = sig
                    }
                    : new
                    {
                        functionCall = new
                        {
                            name = tc.Name,
                            args = JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson)
                        }
                    }).ToArray()
            };
        }

        return new { role, parts = new[] { new { text = message.Content ?? string.Empty } } };
    }

    private ChatCompletionResult ParseResponse(JsonElement payload)
    {
        if (!payload.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            throw new ChatCompletionProviderException(ProviderName, "Gemini response had no candidates.");
        }

        var content = candidates[0].GetProperty("content");
        var parts = content.GetProperty("parts");

        var toolCalls = new List<ToolCall>();
        string? text = null;

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var functionCall))
            {
                var name = functionCall.GetProperty("name").GetString() ?? string.Empty;
                var args = functionCall.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                // thoughtSignature sits alongside functionCall on the same part, not inside it - must be
                // captured here so ToGeminiContent can echo it back on the next round (see
                // ToolCall.GeminiThoughtSignature).
                var signature = part.TryGetProperty("thoughtSignature", out var sigProp) ? sigProp.GetString() : null;
                toolCalls.Add(new ToolCall
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    ArgumentsJson = args,
                    GeminiThoughtSignature = signature
                });
            }
            else if (part.TryGetProperty("text", out var textPart))
            {
                // Gemini's "thinking" models return their reasoning as its own part, marked
                // `"thought": true`, ahead of the actual answer part in the same array - both have a
                // `text` property, so without this check we'd silently concatenate the model's internal
                // reasoning onto the front of every reply (confirmed against a real response: "The user
                // said 'hi'... I should respond politely.Hello! How can I help...", no separator between
                // the two, because that's literally just two text parts joined with nothing in between).
                if (part.TryGetProperty("thought", out var thoughtFlag) && thoughtFlag.ValueKind == JsonValueKind.True)
                {
                    continue;
                }
                text = (text ?? string.Empty) + textPart.GetString();
            }
        }

        return new ChatCompletionResult
        {
            ProviderName = ProviderName,
            FinishReason = toolCalls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Text = text,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }
}
