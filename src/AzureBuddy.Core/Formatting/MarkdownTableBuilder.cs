using System.Text;

namespace AzureBuddy.Core.Formatting;

/// <summary>Builds the "| ID | Title | Type | State |" style tables the n8n workflow's Code nodes
/// (Build View Table, Build My Items Table) and the AI Agent's system prompt both produce.</summary>
public static class MarkdownTableBuilder
{
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n");
        sb.Append('|').Append(string.Concat(headers.Select(_ => "---|"))).Append('\n');

        foreach (var row in rows)
        {
            sb.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
        }

        return sb.ToString().TrimEnd('\n');
    }
}
