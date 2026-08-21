namespace AzureBuddy.Core.Intent;

/// <summary>
/// Single source of truth for the wire-format JSON name of each <see cref="ChatIntent"/> value - used
/// by IntentExtractor.ParseIntent to turn the LLM's response back into the enum. Previously that
/// mapping was a bare string switch with no compiler tie to the enum: adding a seventh intent could
/// add an enum value and forget the switch case with no build error. The static constructor below
/// fails fast (at app/test startup, not on first use) if a <see cref="ChatIntent"/> value is ever added
/// without a corresponding entry here.
/// </summary>
public static class ChatIntentNames
{
    private static readonly IReadOnlyDictionary<ChatIntent, string> ByIntent = new Dictionary<ChatIntent, string>
    {
        [ChatIntent.CreateBug] = "create_bug",
        [ChatIntent.ViewBugs] = "view_bugs",
        [ChatIntent.UpdateItem] = "update_item",
        [ChatIntent.MyItems] = "my_items",
        [ChatIntent.PrioritizeWorkItems] = "prioritize_work_items",
        [ChatIntent.Other] = "other",
    };

    private static readonly IReadOnlyDictionary<string, ChatIntent> ByName =
        ByIntent.ToDictionary(kv => kv.Value, kv => kv.Key);

    static ChatIntentNames()
    {
        var missing = Enum.GetValues<ChatIntent>().Where(v => !ByIntent.ContainsKey(v)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"ChatIntentNames is missing a JSON name for: {string.Join(", ", missing)}. Add an entry in ChatIntentNames.ByIntent.");
        }
    }

    /// <summary>Maps the LLM's raw "intent" string back to the enum, defaulting to
    /// <see cref="ChatIntent.Other"/> for anything unrecognized - same fallback ParseIntent's switch
    /// used for its default arm.</summary>
    public static ChatIntent Parse(string? value) =>
        value is not null && ByName.TryGetValue(value, out var intent) ? intent : ChatIntent.Other;

    public static string ToJsonName(ChatIntent intent) => ByIntent[intent];
}
