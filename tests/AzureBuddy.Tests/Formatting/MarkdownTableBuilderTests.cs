using AzureBuddy.Core.Formatting;
using Xunit;

namespace AzureBuddy.Tests.Formatting;

public class MarkdownTableBuilderTests
{
    [Fact]
    public void Build_ProducesHeaderAndSeparatorRow()
    {
        var table = MarkdownTableBuilder.Build(
            new[] { "ID", "Title" },
            Array.Empty<IReadOnlyList<string>>());

        var lines = table.Split('\n');
        Assert.Equal("| ID | Title |", lines[0]);
        Assert.Equal("|---|---|", lines[1]);
    }

    [Fact]
    public void Build_WithRows_FormatsEachRowPipeDelimited()
    {
        var table = MarkdownTableBuilder.Build(
            new[] { "ID", "Title", "State" },
            new[]
            {
                new[] { "101", "Login bug", "Active" },
                new[] { "102", "Report crash", "Resolved" }
            });

        var lines = table.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("| 101 | Login bug | Active |", lines[2]);
        Assert.Equal("| 102 | Report crash | Resolved |", lines[3]);
    }

    [Fact]
    public void Build_NoTrailingNewline()
    {
        var table = MarkdownTableBuilder.Build(
            new[] { "ID" },
            new[] { new[] { "1" } });

        Assert.False(table.EndsWith('\n'));
    }
}
