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
