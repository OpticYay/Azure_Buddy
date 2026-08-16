using System.Globalization;

namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Single shared urgency-scoring/ranking function, used by MyItemsFlow, ViewBugsFlow, and
/// GetPrioritizedWorkItemsFlow (plus AdoWorkItemToolset's agent-facing tools) so "what's most urgent"
/// logic lives in exactly one place rather than being reimplemented per flow.
///
/// Ranking rule (most urgent first), and the reasoning behind each step:
///
///   1. BUCKET by due-date status: overdue items rank above everything else, regardless of priority.
///      A late item needs attention now no matter how it was originally triaged - an old Priority 3
///      item that's a week overdue is a bigger problem right now than a fresh Priority 1 item that
///      isn't due for a month.
///   2. Within the same bucket, lower ADO Priority number wins (1 = highest, matching ADO's own
///      convention). Priority is a deliberate human judgment call already recorded in the tool, so -
///      once "is this already late" has been decided by the bucket above - it outranks raw date math
///      as the next signal.
///   3. Within the same bucket and priority, whichever item is due sooner sorts first (for overdue
///      items, that means whichever has been overdue the longest sorts first, since "days until due"
///      is more negative the further in the past the deadline was).
///   4. Items with no due date at all form their own bucket, below every item that has a real
///      deadline (a concrete due date should never be outranked by "unknown"), but are still ordered
///      by priority among themselves rather than left in arbitrary order.
///
/// Missing priority is treated as least-urgent within whatever bucket it lands in - never guessed,
/// never treated as either "most" or "least" urgent globally, just sorted last among its peers.
/// </summary>
public static class WorkItemUrgencyRanker
{
    /// <summary>Missing priority sorts as least-urgent within its bucket.</summary>
    private const int MissingPriorityRank = int.MaxValue;

    public static IReadOnlyList<WorkItem> SortByUrgency(IEnumerable<WorkItem> items, DateTime? referenceDate = null)
    {
        var today = (referenceDate ?? DateTime.UtcNow).Date;
        return items
            .Select(item => (Item: item, Key: BuildKey(item, today)))
            .OrderBy(x => x.Key)
            .Select(x => x.Item)
            .ToList();
    }

    public static bool IsOverdue(WorkItem item, DateTime? referenceDate = null)
    {
        var today = (referenceDate ?? DateTime.UtcNow).Date;
        return TryParseDueDate(item, out var due) && due.Date < today;
    }

    public static bool TryParseDueDate(WorkItem item, out DateTime dueDate) =>
        TryParseDate(item.DueDate, out dueDate);

    public static bool TryParseStartDate(WorkItem item, out DateTime startDate) =>
        TryParseDate(item.StartDate, out startDate);

    public static int? TryParsePriority(WorkItem item) =>
        int.TryParse(item.Priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority) ? priority : null;

    /// <summary>Formats a raw ADO date field for table display, or "—" if missing/unparseable -
    /// never throws, never shows a raw ISO timestamp to the user.</summary>
    public static string FormatDate(string? raw) =>
        TryParseDate(raw, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    public static string FormatPriority(WorkItem item) => item.Priority is { Length: > 0 } p ? p : "—";

    /// <summary>Overdue/due-this-week counts for the "what should I work on first" summary line.</summary>
    public static UrgencySummaryCounts Summarize(IEnumerable<WorkItem> items, DateTime? referenceDate = null)
    {
        var today = (referenceDate ?? DateTime.UtcNow).Date;
        var overdue = 0;
        var dueThisWeek = 0;

        foreach (var item in items)
        {
            if (!TryParseDueDate(item, out var due))
            {
                continue;
            }

            var daysUntilDue = (due.Date - today).TotalDays;
            if (daysUntilDue < 0)
            {
                overdue++;
            }
            else if (daysUntilDue <= 7)
            {
                dueThisWeek++;
            }
        }

        return new UrgencySummaryCounts(overdue, dueThisWeek);
    }

    private static bool TryParseDate(string? raw, out DateTime date) =>
        DateTime.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out date);

    private static (int Bucket, int Priority, double DueProximityDays, int Id) BuildKey(WorkItem item, DateTime today)
    {
        var priority = TryParsePriority(item) ?? MissingPriorityRank;

        if (!TryParseDueDate(item, out var due))
        {
            // Bucket 2: no due date at all - never outranks a dated item, but still ordered by priority.
            return (2, priority, 0, item.Id);
        }

        var daysUntilDue = (due.Date - today).TotalDays;
        var bucket = daysUntilDue < 0 ? 0 : 1; // 0 = overdue, 1 = due-dated but not yet overdue

        // Ascending daysUntilDue naturally puts the most-overdue item first within bucket 0 (most
        // negative) and the soonest deadline first within bucket 1 (smallest positive) - one formula
        // serves both same-priority tiebreaks.
        return (bucket, priority, daysUntilDue, item.Id);
    }
}

public readonly record struct UrgencySummaryCounts(int OverdueCount, int DueThisWeekCount);
