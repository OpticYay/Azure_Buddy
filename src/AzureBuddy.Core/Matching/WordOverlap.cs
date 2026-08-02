namespace AzureBuddy.Core.Matching;

/// <summary>Shared Jaccard word-overlap scoring used by both ParentResolver and DuplicateDetector
/// (both n8n Code nodes defined their own near-identical helper; consolidated here).</summary>
internal static class WordOverlap
{
    public static double Jaccard(string a, string b)
    {
        var wordsA = Words(a);
        var wordsB = Words(b);

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Words(string text) =>
        (text ?? string.Empty)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
}
