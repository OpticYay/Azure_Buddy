namespace AzureBuddy.Core.Matching;

public sealed record ExistingBug(int Id, string Title);

public sealed record ScoredBug(int Id, string Title, double Score);

public sealed record DuplicateCheckResult(bool DuplicateFound, ScoredBug? BestMatch);

/// <summary>
/// Ports the n8n "Duplicate Check" Code node verbatim: pure Jaccard word-overlap between the new bug
/// title and each existing open bug's title under the same parent, flagged as a likely duplicate at
/// >= 0.45 overlap. Unlike ParentResolver this has no exact/substring shortcut and no runner-up margin -
/// it only needs the single best match.
/// </summary>
public static class DuplicateDetector
{
    private const double DuplicateThreshold = 0.45;

    public static DuplicateCheckResult FindDuplicate(string newTitle, IReadOnlyList<ExistingBug> existingBugs)
    {
        var normalizedNewTitle = (newTitle ?? string.Empty).ToLowerInvariant();

        ScoredBug? best = null;
        foreach (var bug in existingBugs)
        {
            var score = WordOverlap.Jaccard(normalizedNewTitle, bug.Title);
            if (best is null || score > best.Score)
            {
                best = new ScoredBug(bug.Id, bug.Title, score);
            }
        }

        var duplicateFound = best is not null && best.Score >= DuplicateThreshold;
        return new DuplicateCheckResult(duplicateFound, best);
    }
}
