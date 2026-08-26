using System.Text.RegularExpressions;

namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Turns a model-authored WIQL WHERE-clause fragment into a full, project-scoped query - "compose,
/// don't validate". An earlier design took a whole WIQL string from the model and checked it CONTAINED
/// "[System.TeamProject] = @project", which is trivially defeated (`... OR [System.TeamProject] = 'X'`,
/// `NOT(...)`, hiding the literal inside a string literal) and WiqlQueryBuilder.cs already documents that
/// WIQL does not inherit project scope from the project-scoped REST URL. Instead the model only ever
/// supplies the condition that goes inside the parentheses below - project scope is structural, not
/// checkable, because this class is the only thing that ever writes the SELECT/FROM/WHERE/ORDER BY shape.
/// </summary>
public static class WiqlFragmentCompiler
{
    private const string ProjectScope = "[System.TeamProject] = @project";

    // FROM/ORDER BY would let a fragment escape the WHERE clause entirely (a second SELECT is not legal
    // WIQL syntax here, but FROM/ORDER BY closing out the composed WHERE and starting a new clause is).
    // MODE and ASOF are legal WIQL keywords that change the semantics of the WHOLE query (link-mode
    // traversal, historical snapshot) rather than filtering rows - both are checked for anywhere in the
    // fragment, including inside a string literal, since a literal is not a scope boundary WIQL itself
    // enforces (unlike a real parameterized query, WIQL has no way to make a keyword "just text").
    private static readonly string[] ForbiddenKeywords = { "FROM", "ORDER BY", "MODE", "ASOF" };

    public static WiqlFragmentCompilationResult Compile(string? fragment)
    {
        fragment = (fragment ?? string.Empty).Trim();

        var error = Validate(fragment);
        return error is not null
            ? WiqlFragmentCompilationResult.Failure(error)
            : WiqlFragmentCompilationResult.Succeeded(
                $"SELECT [System.Id] FROM WorkItems WHERE {ProjectScope} AND ({fragment}) ORDER BY [System.ChangedDate] DESC");
    }

    private static string? Validate(string fragment)
    {
        if (fragment.Length == 0)
        {
            return "where_clause is required - pass a filter condition only, e.g. \"[System.AssignedTo] = 'jane@example.com'\" (no SELECT/FROM/ORDER BY - the full query is composed for you).";
        }

        // An unbalanced quote lets the fragment's first quote pair close early and everything after it
        // - including the wrapping ")" the composer adds - gets swallowed into what WIQL parses as plain
        // (no-longer-quoted) query text, defeating the parenthesization below.
        if (fragment.Count(c => c == '\'') % 2 != 0)
        {
            return "where_clause has an unbalanced single quote.";
        }

        if (fragment.Count(c => c == '(') != fragment.Count(c => c == ')'))
        {
            return "where_clause has unbalanced parentheses.";
        }

        foreach (var keyword in ForbiddenKeywords)
        {
            // Escape each word separately, then join with \s+ - Regex.Escape itself escapes whitespace
            // (e.g. "ORDER BY" -> "ORDER\ BY"), so escaping the whole keyword first and replacing spaces
            // afterwards mangles the result into a pattern that can never match ("ORDER\\s+BY").
            var pattern = string.Join(@"\s+", keyword.Split(' ').Select(Regex.Escape));
            if (Regex.IsMatch(fragment, $@"(?<![\w])" + pattern + @"(?![\w])", RegexOptions.IgnoreCase))
            {
                return $"where_clause may not contain '{keyword}' - pass only a filter condition. The query (SELECT/FROM/project scope/ORDER BY) is composed for you; do not try to author it yourself.";
            }
        }

        return null;
    }
}

public sealed record WiqlFragmentCompilationResult(bool Success, string? Query, string? Error)
{
    public static WiqlFragmentCompilationResult Succeeded(string query) => new(true, query, null);
    public static WiqlFragmentCompilationResult Failure(string error) => new(false, null, error);
}
