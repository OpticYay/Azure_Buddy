using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Wire shape for a cached chat window. SchemaVersion lets a future format change be detected
/// and treated as a cache miss (see ChatWindowSerialization.Deserialize) instead of a crash.</summary>
internal sealed record ChatWindowEnvelope(int SchemaVersion, IReadOnlyList<ChatMessage> Messages);

/// <summary>Serializes the messages of a chat window to/from the JSON blob stored in Redis. Deliberately
/// does not serialize ChatHistory itself (private backing list, get-only WindowSize, no parameterless
/// ctor) - the envelope carries only the plain message list, which callers rehydrate into a ChatHistory.</summary>
internal static class ChatWindowSerialization
{
    public const int CurrentSchemaVersion = 1;

    // Static/readonly per the .NET 8 requirement: JsonSerializerOptions caches converter metadata on
    // first use and throws InvalidOperationException if mutated afterwards, so this must be built once,
    // not per call.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(IReadOnlyList<ChatMessage> messages)
    {
        var envelope = new ChatWindowEnvelope(CurrentSchemaVersion, messages);
        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>Null on any failure to produce a usable window - malformed JSON, a truncated payload that
    /// trips a `required` member, or a SchemaVersion this build doesn't recognize - so every caller can
    /// treat "couldn't read the cache" uniformly as a cache miss rather than special-casing each cause.</summary>
    public static IReadOnlyList<ChatMessage>? Deserialize(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ChatWindowEnvelope>(json, Options);
            if (envelope is null || envelope.SchemaVersion != CurrentSchemaVersion)
            {
                return null;
            }

            return envelope.Messages;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
