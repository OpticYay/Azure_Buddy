using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Formatting;
using AzureBuddy.Core.Intent;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>
/// Dedicated "what should I work on first" command: fetches the user's own assigned work items (same
/// WIQL as MyItemsFlow), ranks them with the same shared <see cref="WorkItemUrgencyRanker"/>, and
/// returns them as a table led by a short natural-language summary (overdue / due-this-week counts) -
/// unlike MyItemsFlow, whose ranking is implicit in row order, this command exists specifically to
/// answer "what's most urgent" as a question, not just list items.
/// </summary>
public sealed class GetPrioritizedWorkItemsFlow
{
    private static readonly string[] Fields =
    {
        AdoFields.Title, AdoFields.WorkItemType, AdoFields.State,
        AdoFields.Priority, AdoFields.StartDate, AdoFields.TargetDate, AdoFields.DueDate, AdoFields.FinishDate
    };

    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;

    public GetPrioritizedWorkItemsFlow(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
    }

    public async Task<FlowResult> ExecuteAsync(ExtractedIntent extracted, CancellationToken cancellationToken = default)
    {
        var connection = _connectionAccessor.Require();

        var ids = await _adoClient.QueryWiqlAsync(
            connection,
            WiqlQueryBuilder.AssignedToMe(extracted.State, extracted.WorkItemTypeFilter),
            cancellationToken);

        if (ids.Count == 0)
        {
            var noun = string.IsNullOrEmpty(extracted.WorkItemTypeFilter) ? "work items" : $"{extracted.WorkItemTypeFilter} items";
            return FlowResult.Done($"You have no assigned {noun} to prioritize.");
        }

        var items = await _adoClient.GetWorkItemsAsync(connection, ids, Fields, cancellationToken);
        var ranked = WorkItemUrgencyRanker.SortByUrgency(items);
        var summary = WorkItemUrgencyRanker.Summarize(ranked);

        var headers = new[] { "ID", "Title", "Type", "State", "Priority", "Start Date", "Due Date" };
        var rows = ranked.Select(BuildRow).ToList();
        var table = MarkdownTableBuilder.Build(headers, rows);

        var summaryLine = BuildSummaryLine(summary, ranked.Count);
        return FlowResult.DoneWithTable($"{summaryLine}\n\n{table}", headers, rows);
    }

    private static IReadOnlyList<string> BuildRow(WorkItem i) => new[]
    {
        i.Id.ToString(), i.Title ?? "", i.WorkItemType ?? "", i.State ?? "",
        WorkItemUrgencyRanker.FormatPriority(i), WorkItemUrgencyRanker.FormatDate(i.StartDate), WorkItemUrgencyRanker.FormatDate(i.DueDate)
    };

    private static string BuildSummaryLine(UrgencySummaryCounts summary, int total)
    {
        var parts = new List<string>();
        if (summary.OverdueCount > 0)
        {
            parts.Add($"{summary.OverdueCount} overdue item{(summary.OverdueCount == 1 ? "" : "s")}");
        }
        if (summary.DueThisWeekCount > 0)
        {
            parts.Add($"{summary.DueThisWeekCount} due within the next 7 days");
        }

        if (parts.Count == 0)
        {
            return $"You have {total} assigned work item{(total == 1 ? "" : "s")}, ranked most to least urgent below - none are overdue or due within the next 7 days.";
        }

        return $"You have {string.Join(" and ", parts)} (out of {total} assigned work item{(total == 1 ? "" : "s")}), ranked most to least urgent below.";
    }
}
