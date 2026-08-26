using AzureBuddy.Core.AzureDevOps;

namespace AzureBuddy.Core.WorkItemStates;

/// <summary>Shared state-change validation: fetch the work item's type, look up the admin-configured
/// enabled states for that type, and case-insensitively match the requested state. Previously
/// duplicated between UpdateItemFlow.ValidateStateAsync and AdoWorkItemToolset.UpdateWorkItemAsync's
/// inline block - each call site adapts <see cref="WorkItemStateValidationResult"/> to its own output
/// shape.</summary>
public static class WorkItemStateValidator
{
    public static async Task<WorkItemStateValidationResult> ValidateAsync(
        IAdoClient adoClient,
        WorkItemStateConfigService stateConfigService,
        AdoConnectionContext connection,
        int workItemId,
        string requestedState,
        CancellationToken cancellationToken)
    {
        var items = await adoClient.GetWorkItemsAsync(connection, new[] { workItemId }, new[] { AdoFields.WorkItemType }, cancellationToken);
        var workItemType = items.FirstOrDefault()?.WorkItemType;
        if (string.IsNullOrEmpty(workItemType))
        {
            return WorkItemStateValidationResult.NoConfig();
        }

        var validStates = await stateConfigService.GetEnabledStateNamesAsync(workItemType, cancellationToken);
        if (validStates.Count == 0)
        {
            return WorkItemStateValidationResult.NoConfig();
        }

        var match = validStates.FirstOrDefault(s => string.Equals(s, requestedState, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? WorkItemStateValidationResult.Valid(match)
            : WorkItemStateValidationResult.Invalid(workItemType, validStates);
    }
}

/// <summary><see cref="HasConfig"/> is false when there's nothing configured to validate against yet
/// (no admin config for this work item type, or the type couldn't be resolved) - callers treat that as
/// "nothing to validate", not as a rejection, and let Azure DevOps itself accept or reject the state.
/// When <see cref="HasConfig"/> is true, <see cref="IsValid"/> distinguishes a match (with the
/// canonical-cased <see cref="NormalizedState"/>) from a rejection (with <see cref="WorkItemType"/> and
/// <see cref="ValidStates"/> for the caller to report back).</summary>
public sealed record WorkItemStateValidationResult(
    bool HasConfig,
    bool IsValid,
    string? NormalizedState,
    string? WorkItemType,
    IReadOnlyList<string>? ValidStates)
{
    public static WorkItemStateValidationResult NoConfig() => new(false, true, null, null, null);
    public static WorkItemStateValidationResult Valid(string normalizedState) => new(true, true, normalizedState, null, null);
    public static WorkItemStateValidationResult Invalid(string workItemType, IReadOnlyList<string> validStates) =>
        new(true, false, null, workItemType, validStates);
}
