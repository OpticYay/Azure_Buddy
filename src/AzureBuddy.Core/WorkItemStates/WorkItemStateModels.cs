using System.ComponentModel.DataAnnotations;

namespace AzureBuddy.Core.WorkItemStates;

public sealed record WorkItemStateView(int Id, string WorkItemType, string StateName, int DisplayOrder, bool IsEnabled, DateTime UpdatedAt);

public sealed record WorkItemTypeStatesView(string WorkItemType, IReadOnlyList<WorkItemStateView> States);

public sealed class CreateWorkItemStateRequest
{
    [Required, StringLength(128)]
    public string WorkItemType { get; set; } = string.Empty;

    [Required, StringLength(128)]
    public string StateName { get; set; } = string.Empty;

    public int DisplayOrder { get; set; }

    public bool IsEnabled { get; set; } = true;
}

public sealed class UpdateWorkItemStateRequest
{
    [Required, StringLength(128)]
    public string StateName { get; set; } = string.Empty;

    public int DisplayOrder { get; set; }

    public bool IsEnabled { get; set; } = true;
}
