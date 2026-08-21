using AzureBuddy.Core.AzureDevOps;
using Xunit;

namespace AzureBuddy.Tests.AzureDevOps;

public class WiqlQueryBuilderTests
{
    [Fact]
    public void SearchByTitle_IncludesTermAndExcludesClosedItems()
    {
        var query = WiqlQueryBuilder.SearchByTitle("SOA report");

        Assert.Contains("[System.Title] CONTAINS 'SOA report'", query);
        Assert.Contains("[System.State] <> 'Closed'", query);
        Assert.Contains("ORDER BY [System.CreatedDate] DESC", query);
    }

    [Fact]
    public void SearchByTitle_EscapesSingleQuotes()
    {
        var query = WiqlQueryBuilder.SearchByTitle("O'Brien's task");

        Assert.Contains("CONTAINS 'O''Brien''s task'", query);
    }

    // WIQL doesn't inherit project scope from the project-scoped REST URL it's posted to - without an
    // explicit [System.TeamProject] clause these queries run across the entire organization.
    [Fact]
    public void EveryQuery_IsScopedToTheCallersProject()
    {
        Assert.Contains("[System.TeamProject] = @project", WiqlQueryBuilder.SearchByTitle("anything"));
        Assert.Contains("[System.TeamProject] = @project", WiqlQueryBuilder.SearchByTitleWords(new[] { "anything" }));
        Assert.Contains("[System.TeamProject] = @project", WiqlQueryBuilder.ChildrenOf(1));
        Assert.Contains("[System.TeamProject] = @project", WiqlQueryBuilder.AssignedToMe(null));
    }

    [Fact]
    public void SearchByTitleWords_RequiresEveryWordButNotTheirOrder()
    {
        var query = WiqlQueryBuilder.SearchByTitleWords(new[] { "login", "screen" });

        Assert.Contains("[System.Title] CONTAINS 'login' AND [System.Title] CONTAINS 'screen'", query);
        // System.Title is a String field; CONTAINS WORDS is PlainText-only and would be rejected as a 400.
        Assert.DoesNotContain("CONTAINS WORDS", query);
    }

    [Fact]
    public void SearchByTitleWords_EscapesEachWord()
    {
        Assert.Contains("CONTAINS 'O''Brien'", WiqlQueryBuilder.SearchByTitleWords(new[] { "O'Brien" }));
    }

    [Theory]
    // The phrase an LLM extracts from a sentence carries work-item vocabulary that is almost never in
    // the title itself; matching on it verbatim is what made these searches come back empty.
    [InlineData("login screen user story", new[] { "login", "screen" })]
    [InlineData("dummy user story for bot testing workitem", new[] { "dummy", "bot", "testing" })]
    [InlineData("get me all bugs under SOA report", new[] { "SOA", "report" })]
    public void SearchWords_DropsWorkItemVocabularyAndPunctuation(string phrase, string[] expected)
    {
        Assert.Equal(expected, WiqlQueryBuilder.SearchWords(phrase));
    }

    [Fact]
    public void SearchWords_AllNoise_ReturnsNothingToSearchOn()
    {
        // Caller must treat this as "don't run a widened query" - an empty word list would otherwise
        // build `WHERE (...)` with an empty clause and 400.
        Assert.Empty(WiqlQueryBuilder.SearchWords("get me the bug"));
    }

    [Fact]
    public void ChildrenOf_FiltersByParentId()
    {
        var query = WiqlQueryBuilder.ChildrenOf(12345);

        Assert.Contains("[System.Parent] = 12345", query);
        Assert.Contains("ORDER BY [System.CreatedDate] DESC", query);
    }

    [Fact]
    public void AssignedToMe_NoState_ExcludesClosedOnly()
    {
        var query = WiqlQueryBuilder.AssignedToMe(null);

        Assert.Contains("[System.AssignedTo] = @Me", query);
        Assert.Contains("[System.State] <> 'Closed'", query);
        Assert.DoesNotContain("[System.State] =", query);
    }

    [Fact]
    public void AssignedToMe_WithState_FiltersToThatStateExactly()
    {
        var query = WiqlQueryBuilder.AssignedToMe("Active");

        Assert.Contains("[System.State] = 'Active'", query);
        Assert.DoesNotContain("<> 'Closed'", query);
    }

    [Fact]
    public void AssignedToMe_EmptyState_TreatedSameAsNull()
    {
        var query = WiqlQueryBuilder.AssignedToMe(string.Empty);

        Assert.Contains("[System.State] <> 'Closed'", query);
    }

    [Fact]
    public void AssignedToMe_NoWorkItemType_ReturnsEveryType()
    {
        // Regression: "get me all the bugs assigned to me" was returning every type, not just Bugs,
        // because this filter didn't exist - the omitted-type case must still produce a valid query
        // with no type clause at all (not an empty/broken one).
        var query = WiqlQueryBuilder.AssignedToMe(null);

        Assert.DoesNotContain("[System.WorkItemType] =", query);
    }

    [Fact]
    public void AssignedToMe_WithWorkItemType_FiltersToThatTypeExactly()
    {
        var query = WiqlQueryBuilder.AssignedToMe(null, "Bug");

        Assert.Contains("[System.WorkItemType] = 'Bug'", query);
    }

    [Fact]
    public void AssignedToMe_StateAndWorkItemTypeTogether_FiltersOnBoth()
    {
        var query = WiqlQueryBuilder.AssignedToMe("Active", "Task");

        Assert.Contains("[System.State] = 'Active'", query);
        Assert.Contains("[System.WorkItemType] = 'Task'", query);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("Open")]
    [InlineData(" OPEN ")]
    public void AssignedToMe_OpenState_TreatedSameAsNoState(string state)
    {
        // "open" isn't a real ADO state on any standard process template - filtering for it literally
        // (`[System.State] = 'open'`) would always return zero rows instead of "everything not closed."
        var query = WiqlQueryBuilder.AssignedToMe(state);

        Assert.Contains("[System.State] <> 'Closed'", query);
        Assert.DoesNotContain("[System.State] = 'open'", query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("it's", "it''s")]
    [InlineData("''already''", "''''already''''")]
    public void Escape_DoublesSingleQuotes(string? input, string expected)
    {
        Assert.Equal(expected, WiqlQueryBuilder.Escape(input));
    }

    // Adversarial cases per 03-data-and-performance.md §3.8: Escape() is the sole barrier against WIQL
    // injection through a title an attacker controls (e.g. a work item title used as CONTAINS input).
    // Each case asserts the escaped output cannot terminate the surrounding string literal early.

    [Fact]
    public void Escape_QuoteImmediatelyFollowedByWiqlKeyword_CannotTerminateTheStringLiteralEarly()
    {
        // A naive single-pass or non-doubling escape could let `' OR '1'='1` style payloads close the
        // literal early; doubling every quote (SearchByTitle wraps the escaped value in single quotes)
        // means the whole payload stays inert data inside one literal, however it's shaped.
        var payload = "x' OR '1'='1";
        var query = WiqlQueryBuilder.SearchByTitle(payload);

        Assert.Contains("CONTAINS 'x'' OR ''1''=''1'", query);
        // The literal opened by CONTAINS ' must be the same one closed just before AND - i.e. exactly
        // one (escaped) literal, not the payload's quotes prematurely closing and reopening it.
        var containsIndex = query.IndexOf("CONTAINS '", StringComparison.Ordinal);
        var afterOpenQuote = containsIndex + "CONTAINS '".Length;
        var closingQuoteIndex = FindUnescapedClosingQuote(query, afterOpenQuote);
        Assert.Equal("x'' OR ''1''=''1", query.Substring(afterOpenQuote, closingQuoteIndex - afterOpenQuote));
    }

    [Fact]
    public void Escape_MultipleAdjacentQuotes_EachOneIsIndividuallyDoubled()
    {
        Assert.Equal("''''''", WiqlQueryBuilder.Escape("'''"));
    }

    [Fact]
    public void Escape_QuoteAdjacentToOtherWiqlSignificantCharacters_StaysInertData()
    {
        // Brackets and parens are WIQL-significant elsewhere (field references, grouping) but Escape's
        // only job is quote-doubling - confirm a quote sitting directly next to them still doubles
        // correctly and doesn't get treated specially.
        Assert.Equal("[System.Title]'' = ''x", WiqlQueryBuilder.Escape("[System.Title]' = 'x"));
    }

    /// <summary>Walks a WIQL string literal that started right after `start`, honoring `''` as an
    /// escaped quote (not a terminator), and returns the index of the real closing quote.</summary>
    private static int FindUnescapedClosingQuote(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }
                return i;
            }
            i++;
        }
        throw new InvalidOperationException("No closing quote found - malformed test input.");
    }
}
