using AzureBuddy.Core.Formatting;

namespace AzureBuddy.Core.AzureDevOps;

/// <summary>Shared field list, headers, and row-building logic for the work-item markdown tables
/// produced by MyItemsFlow, ViewBugsFlow, and GetPrioritizedWorkItemsFlow - previously each flow
/// declared its own copy of all three.</summary>
public static class WorkItemTableBuilder
{
    public static readonly string[] Fields =
        new[] { AdoFields.Title, AdoFields.WorkItemType, AdoFields.State, AdoFields.Priority, AdoFields.StartDate }
            .Concat(AdoFields.DueDateCandidates)
            .ToArray();

    public static readonly string[] Headers = { "ID", "Title", "Type", "State", "Priority", "Start Date", "Due Date" };

    public static IReadOnlyList<string> BuildRow(WorkItem i) => new[]
    {
        i.Id.ToString(), i.Title ?? "", i.WorkItemType ?? "", i.State ?? "",
        WorkItemUrgencyRanker.FormatPriority(i), WorkItemUrgencyRanker.FormatDate(i.StartDate), WorkItemUrgencyRanker.FormatDate(i.DueDate)
    };

    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string Table) Build(IReadOnlyList<WorkItem> items)
    {
        var rows = items.Select(BuildRow).ToList();
        var table = MarkdownTableBuilder.Build(Headers, rows);
        return (Headers, rows, table);
    }
}
