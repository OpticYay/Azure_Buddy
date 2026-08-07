using AzureBuddy.Core.AzureDevOps;
using Xunit;

namespace AzureBuddy.Tests.AzureDevOps;

public class WorkItemUrgencyRankerTests
{
    private static readonly DateTime Today = new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

    private static WorkItem Item(int id, string? priority = null, string? dueDate = null) => new()
    {
        Id = id,
        Fields = new Dictionary<string, object?>
        {
            ["Microsoft.VSTS.Common.Priority"] = priority,
            ["Microsoft.VSTS.Scheduling.TargetDate"] = dueDate,
        },
    };

    [Fact]
    public void SortByUrgency_OverdueItem_RanksAboveHigherPriorityNonOverdueItem()
    {
        // Priority 3 but overdue should still beat Priority 1 that isn't due yet - overdue status
        // always wins regardless of priority, per the ranking rule.
        var overdueLowPriority = Item(1, priority: "3", dueDate: "2026-07-20T00:00:00Z");
        var futureHighPriority = Item(2, priority: "1", dueDate: "2026-09-01T00:00:00Z");

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { futureHighPriority, overdueLowPriority }, Today);

        Assert.Equal(1, ranked[0].Id);
        Assert.Equal(2, ranked[1].Id);
    }

    [Fact]
    public void SortByUrgency_SameBucket_LowerPriorityNumberRanksFirst()
    {
        var priority2 = Item(1, priority: "2", dueDate: "2026-09-01T00:00:00Z");
        var priority1 = Item(2, priority: "1", dueDate: "2026-09-05T00:00:00Z");

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { priority2, priority1 }, Today);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void SortByUrgency_SamePriorityBothOverdue_MostOverdueRanksFirst()
    {
        var overdueByOneDay = Item(1, priority: "1", dueDate: "2026-08-04T00:00:00Z");
        var overdueByTenDays = Item(2, priority: "1", dueDate: "2026-07-26T00:00:00Z");

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { overdueByOneDay, overdueByTenDays }, Today);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void SortByUrgency_SamePriorityBothDueDated_SoonerDueDateRanksFirst()
    {
        var dueLater = Item(1, priority: "2", dueDate: "2026-09-20T00:00:00Z");
        var dueSoon = Item(2, priority: "2", dueDate: "2026-08-10T00:00:00Z");

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { dueLater, dueSoon }, Today);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void SortByUrgency_NoDueDate_NeverOutranksAnItemWithARealDueDate()
    {
        // Priority 1 with no due date should still sort BELOW a Priority 4 item that has a concrete
        // deadline - a real due date beats "unknown" even when priority alone would say otherwise.
        var noDueDateHighPriority = Item(1, priority: "1", dueDate: null);
        var datedLowPriority = Item(2, priority: "4", dueDate: "2026-12-01T00:00:00Z");

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { noDueDateHighPriority, datedLowPriority }, Today);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void SortByUrgency_MultipleWithNoDueDate_StillOrderedByPriorityAmongThemselves()
    {
        var noDatePriority3 = Item(1, priority: "3", dueDate: null);
        var noDatePriority1 = Item(2, priority: "1", dueDate: null);

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { noDatePriority3, noDatePriority1 }, Today);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void SortByUrgency_MissingPriority_TreatedAsLeastUrgentWithinItsBucket_NeverThrows()
    {
        var missingPriority = Item(1, priority: null, dueDate: "2026-08-10T00:00:00Z");
        var explicitPriority = Item(2, priority: "4", dueDate: "2026-08-10T00:00:00Z");

        var ranked = WorkItemUrgencyRanker.SortByUrgency(new[] { missingPriority, explicitPriority }, Today);

        Assert.Equal(2, ranked[0].Id);
        Assert.Equal(1, ranked[1].Id);
    }

    [Fact]
    public void IsOverdue_DueDateInPast_ReturnsTrue()
    {
        var item = Item(1, dueDate: "2026-07-01T00:00:00Z");
        Assert.True(WorkItemUrgencyRanker.IsOverdue(item, Today));
    }

    [Fact]
    public void IsOverdue_NoDueDate_ReturnsFalse()
    {
        var item = Item(1, dueDate: null);
        Assert.False(WorkItemUrgencyRanker.IsOverdue(item, Today));
    }

    [Fact]
    public void Summarize_CountsOverdueAndDueWithinSevenDaysSeparately()
    {
        var overdue = Item(1, dueDate: "2026-08-01T00:00:00Z");
        var dueThisWeek = Item(2, dueDate: "2026-08-08T00:00:00Z");
        var dueLater = Item(3, dueDate: "2026-09-01T00:00:00Z");
        var noDueDate = Item(4, dueDate: null);

        var summary = WorkItemUrgencyRanker.Summarize(new[] { overdue, dueThisWeek, dueLater, noDueDate }, Today);

        Assert.Equal(1, summary.OverdueCount);
        Assert.Equal(1, summary.DueThisWeekCount);
    }

    [Fact]
    public void FormatDate_UnparseableOrMissing_ReturnsEmDash()
    {
        Assert.Equal("—", WorkItemUrgencyRanker.FormatDate(null));
        Assert.Equal("—", WorkItemUrgencyRanker.FormatDate(""));
        Assert.Equal("2026-08-10", WorkItemUrgencyRanker.FormatDate("2026-08-10T00:00:00Z"));
    }
}
