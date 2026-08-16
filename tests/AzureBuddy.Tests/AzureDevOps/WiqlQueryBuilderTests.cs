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
}
