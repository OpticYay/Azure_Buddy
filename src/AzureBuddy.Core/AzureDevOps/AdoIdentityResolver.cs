using System.Net;
using System.Text.Json;
using AzureBuddy.Core.Matching;

namespace AzureBuddy.Core.AzureDevOps;

public sealed record ScoredIdentity(ResolvedIdentity Identity, double Score);

public sealed record IdentityResolution(bool Resolved, ResolvedIdentity? Identity, IReadOnlyList<ScoredIdentity> Candidates);

/// <summary>
/// Resolves a free-text name ("John", "jane@example.com") to a concrete Azure DevOps identity, so
/// "bugs assigned to John" can become a WIQL query against System.AssignedTo's actual unique name rather
/// than treating "John" as a literal string match (which fails the moment the display name isn't exactly
/// "John"). Two-tier, because the Identities REST API needs the vso.identity PAT scope, which many
/// PATs granted only "Work Items" access won't have:
///   1. Primary: IAdoClient.SearchIdentitiesAsync (account-wide, authoritative).
///   2. Fallback (only on 401/403 - a scope problem, not "no matches"): run a WIQL
///      [System.AssignedTo] CONTAINS '&lt;name&gt;' query and harvest the distinct assignees off the results.
/// Mirrors Matching/ParentResolver's ambiguity shape (exact/substring/Jaccard scoring, resolved only if
/// the best score clears a floor AND beats the runner-up by a margin) rather than inventing a new one -
/// an ambiguous "John" comes back as Candidates for the caller to ask the user about, never a silent guess.
/// </summary>
public sealed class AdoIdentityResolver
{
    private const double ExactMatchScore = 1.0;
    private const double SubstringMatchScore = 0.8;
    private const double MinResolutionScore = 0.5;
    private const double MinResolutionMargin = 0.15;
    private const int WiqlFallbackMaxCandidates = 50;

    private readonly IAdoClient _adoClient;

    public AdoIdentityResolver(IAdoClient adoClient)
    {
        _adoClient = adoClient;
    }

    public async Task<IdentityResolution> ResolveAsync(AdoConnectionContext connection, string name, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ResolvedIdentity> candidates;
        try
        {
            candidates = await _adoClient.SearchIdentitiesAsync(connection, name, cancellationToken);
        }
        catch (AdoApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            candidates = await ResolveViaWiqlFallbackAsync(connection, name, cancellationToken);
        }

        return Score(candidates, name);
    }

    private async Task<IReadOnlyList<ResolvedIdentity>> ResolveViaWiqlFallbackAsync(AdoConnectionContext connection, string name, CancellationToken cancellationToken)
    {
        var wiql =
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project " +
            $"AND [System.AssignedTo] CONTAINS '{WiqlQueryBuilder.Escape(name)}' " +
            $"ORDER BY [System.ChangedDate] DESC";

        var ids = await _adoClient.QueryWiqlAsync(connection, wiql, cancellationToken);
        if (ids.Count == 0)
        {
            return Array.Empty<ResolvedIdentity>();
        }

        var items = await _adoClient.GetWorkItemsAsync(connection, ids.Take(WiqlFallbackMaxCandidates), new[] { AdoFields.AssignedTo }, cancellationToken);

        return items
            .Select(ParseAssignedTo)
            .Where(a => a is not null)
            .Select(a => a!)
            .DistinctBy(a => a.UniqueName)
            .ToList();
    }

    private static ResolvedIdentity? ParseAssignedTo(WorkItem item)
    {
        if (!item.Fields.TryGetValue(AdoFields.AssignedTo, out var raw) || raw is not JsonElement element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => new ResolvedIdentity(
                element.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                element.TryGetProperty("displayName", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                element.TryGetProperty("uniqueName", out var u) ? u.GetString() ?? string.Empty : string.Empty),
            JsonValueKind.String => new ResolvedIdentity(string.Empty, element.GetString() ?? string.Empty, element.GetString() ?? string.Empty),
            _ => null
        };
    }

    private static IdentityResolution Score(IReadOnlyList<ResolvedIdentity> candidates, string searchTerm)
    {
        var normalizedSearchTerm = (searchTerm ?? string.Empty).ToLowerInvariant().Trim();

        var scored = candidates
            .Select(c => new ScoredIdentity(c, ScoreOne(c.DisplayName, normalizedSearchTerm)))
            .OrderByDescending(c => c.Score)
            .ToList();

        var best = scored.Count > 0 ? scored[0] : null;
        var second = scored.Count > 1 ? scored[1] : null;

        var isResolved = best is not null
            && best.Score >= MinResolutionScore
            && (second is null || best.Score - second.Score >= MinResolutionMargin);

        return new IdentityResolution(isResolved, isResolved ? best!.Identity : null, scored);
    }

    private static double ScoreOne(string displayName, string normalizedSearchTerm)
    {
        var t = (displayName ?? string.Empty).ToLowerInvariant();
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
