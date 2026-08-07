namespace AzureBuddy.Data.Entities;

/// <summary>
/// One admin-defined valid state for one work item type (e.g. WorkItemType="Bug", StateName="Active").
/// The full set of enabled rows for a type, ordered by DisplayOrder, is "the list of states this app
/// will accept for that type" - see WorkItemStateConfigService.GetEnabledStateNamesAsync, which
/// UpdateItemFlow/AdoWorkItemToolset.UpdateWorkItemAsync validate a requested state change against
/// before ever calling Azure DevOps.
///
/// This is deliberately backend-configured rather than fetched live from Azure DevOps's own
/// work-item-type/state metadata API. Tradeoff: simpler (no extra ADO call on every update, works
/// even if the connected PAT lacks permission to read process configuration) and gives an admin full
/// control to deliberately NARROW the allowed states below what ADO itself permits (e.g. hide a
/// state nobody should use from chat) - but it can drift out of sync if someone edits the process
/// template's states directly in Azure DevOps and forgets to update this table to match. Accepted for
/// this pass; see the seed data in the initial migration for the default set this ships with.
/// </summary>
public sealed class WorkItemStateConfiguration
{
    public int Id { get; set; }
    public required string WorkItemType { get; set; }
    public required string StateName { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
