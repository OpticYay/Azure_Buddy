using AzureBuddy.Core.AzureDevOps;
using Xunit;

namespace AzureBuddy.Tests.AzureDevOps;

public class WiqlFragmentCompilerTests
{
    [Fact]
    public void Compile_ValidFragment_WrapsWithProjectScopeAndOrdering()
    {
        var result = WiqlFragmentCompiler.Compile("[System.AssignedTo] = 'jane@example.com'");

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Contains("[System.TeamProject] = @project", result.Query);
        Assert.Contains("([System.AssignedTo] = 'jane@example.com')", result.Query);
        Assert.Contains("ORDER BY [System.ChangedDate] DESC", result.Query);
        Assert.StartsWith("SELECT [System.Id] FROM WorkItems WHERE", result.Query);
    }

    [Fact]
    public void Compile_NullFragment_Fails()
    {
        var result = WiqlFragmentCompiler.Compile(null);

        Assert.False(result.Success);
        Assert.Null(result.Query);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Compile_EmptyOrWhitespaceFragment_Fails()
    {
        var result = WiqlFragmentCompiler.Compile("   ");

        Assert.False(result.Success);
        Assert.Contains("where_clause is required", result.Error);
    }

    [Fact]
    public void Compile_UnbalancedSingleQuote_Fails()
    {
        var result = WiqlFragmentCompiler.Compile("[System.Title] = 'unterminated");

        Assert.False(result.Success);
        Assert.Contains("unbalanced single quote", result.Error);
    }

    [Fact]
    public void Compile_UnbalancedParentheses_Fails()
    {
        var result = WiqlFragmentCompiler.Compile("([System.State] = 'Active'");

        Assert.False(result.Success);
        Assert.Contains("unbalanced parentheses", result.Error);
    }

    [Theory]
    [InlineData("[System.Id] = 1 FROM WorkItems")]
    [InlineData("[System.Id] = 1 ORDER BY [System.Id]")]
    [InlineData("[System.Id] = 1 MODE (Recursive)")]
    [InlineData("[System.Id] = 1 ASOF '2024-01-01'")]
    public void Compile_ForbiddenKeyword_Fails(string fragment)
    {
        var result = WiqlFragmentCompiler.Compile(fragment);

        Assert.False(result.Success);
        Assert.Contains("may not contain", result.Error);
    }

    [Fact]
    public void Compile_ForbiddenKeywordInsideStringLiteral_StillFails()
    {
        // Keywords are checked textually, including inside string literals - WIQL doesn't treat a
        // literal as a scope boundary the way a real parameterized query would.
        var result = WiqlFragmentCompiler.Compile("[System.Title] = 'please ORDER BY priority'");

        Assert.False(result.Success);
        Assert.Contains("ORDER BY", result.Error);
    }

    [Fact]
    public void Compile_KeywordSubstringWithinIdentifier_DoesNotFalselyMatch()
    {
        // "FROMAGE" contains "FROM" as a substring but is not the FROM keyword - the word-boundary
        // regex must not flag it.
        var result = WiqlFragmentCompiler.Compile("[System.Title] = 'FROMAGE'");

        Assert.True(result.Success);
    }

    [Fact]
    public void Compile_KeywordCheckIsCaseInsensitive()
    {
        var result = WiqlFragmentCompiler.Compile("[System.Id] = 1 from WorkItems");

        Assert.False(result.Success);
        Assert.Contains("FROM", result.Error);
    }

    [Fact]
    public void Compile_TrimsSurroundingWhitespace()
    {
        var result = WiqlFragmentCompiler.Compile("   [System.State] = 'Active'   ");

        Assert.True(result.Success);
        Assert.Contains("([System.State] = 'Active')", result.Query);
    }
}
