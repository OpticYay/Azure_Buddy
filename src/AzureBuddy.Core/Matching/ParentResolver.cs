namespace AzureBuddy.Core.Matching;

public sealed record ParentCandidate(int Id, string Title, string Type);

public sealed record ScoredParentCandidate(int Id, string Title, string Type, double Score);

public sealed record ParentResolution(bool Resolved, int? ParentId, IReadOnlyList<ScoredParentCandidate> Candidates);

/// <summary>
/// Ports the n8n "Resolve Parent" Code node verbatim: scores each WIQL-search candidate title against
/// the user's search phrase (exact match = 1.0, substring either direction = 0.8, else Jaccard word
/// overlap), then only calls it resolved if the best score clears 0.5 AND beats the runner-up by >= 0.15
/// - otherwise the caller should ask the user to disambiguate rather than guessing.
/// </summary>
public static class ParentResolver
{
    private const double ExactMatchScore = 1.0;
    private const double SubstringMatchScore = 0.8;
    private const double MinResolutionScore = 0.5;
    private const double MinResolutionMargin = 0.15;

    public static ParentResolution Resolve(IReadOnlyList<ParentCandidate> candidates, string searchTerm)
    {
        var normalizedSearchTerm = (searchTerm ?? string.Empty).ToLowerInvariant().Trim();

        var scored = candidates
            .Select(c => new ScoredParentCandidate(c.Id, c.Title, c.Type, Score(c.Title, normalizedSearchTerm)))
            .OrderByDescending(c => c.Score)
            .ToList();

        var best = scored.Count > 0 ? scored[0] : null;
        var second = scored.Count > 1 ? scored[1] : null;

        var isResolved = best is not null
            && best.Score >= MinResolutionScore
            && (second is null || best.Score - second.Score >= MinResolutionMargin);

        return new ParentResolution(isResolved, isResolved ? best!.Id : null, scored);
    }

    private static double Score(string title, string normalizedSearchTerm)
    {
        var t = (title ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(t))
        {
            return 0;
        }

        if (t == normalizedSearchTerm)
        {
            return ExactMatchScore;
        }

        if (t.Contains(normalizedSearchTerm) || normalizedSearchTerm.Contains(t))
        {
            return SubstringMatchScore;
        }

        return WordOverlap.Jaccard(normalizedSearchTerm, t);
    }
}
