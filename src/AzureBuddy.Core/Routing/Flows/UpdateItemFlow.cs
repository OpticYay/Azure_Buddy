using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.WorkItemStates;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Has Update Fields? -> Update Work Item (Code Path) -> Build Update Message". A
/// requested state change is validated against the admin-configured state list for that item's work
/// item type (see WorkItemStateConfiguration.cs) before ever calling Azure DevOps.</summary>
public sealed class UpdateItemFlow
{
    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;
    private readonly WorkItemStateConfigService _stateConfigService;
    private readonly ILogger<UpdateItemFlow> _logger;

    public UpdateItemFlow(
        IAdoClient adoClient,
        AdoConnectionContextAccessor connectionAccessor,
        WorkItemStateConfigService stateConfigService,
        ILogger<UpdateItemFlow> logger)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
        _stateConfigService = stateConfigService;
        _logger = logger;
    }

    public async Task<FlowResult> ExecuteAsync(ExtractedIntent extracted, CancellationToken cancellationToken = default)
    {
        var hasState = !string.IsNullOrEmpty(extracted.State);
        var hasComment = !string.IsNullOrEmpty(extracted.Comment);

        if (!hasState && !hasComment)
        {
            return FlowResult.FallThroughToAgent();
        }

        if (!int.TryParse(extracted.WorkItemId, out var id))
        {
            return FlowResult.FallThroughToAgent();
        }

        var connection = _connectionAccessor.Require();
        var requestedState = extracted.State;

        if (hasState)
        {
            var (normalizedState, rejectionMessage) = await ValidateStateAsync(connection, id, requestedState, cancellationToken);
            if (rejectionMessage is not null)
            {
                // Not a failure - a guided response so the user can immediately pick a correct state,
                // per the admin-configured list for this item's type. Doesn't touch ADO at all.
                return FlowResult.Done(rejectionMessage);
            }
            requestedState = normalizedState ?? requestedState;
        }

        var ops = new List<JsonPatchOperation>();
        if (hasState) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.State}", requestedState));
        if (hasComment) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.History}", extracted.Comment));

        try
        {
            var updated = await _adoClient.UpdateWorkItemAsync(connection, id, ops, cancellationToken);

            var parts = new List<string> { $"Work item #{updated.Id} updated." };
            if (hasState) parts.Add($"New state: {requestedState}.");
            if (hasComment) parts.Add("Comment added.");
            return FlowResult.DoneWithConfirmation(string.Join(" ", parts), updated.Id);
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Failed to update work item {Id}", id);
            return FlowResult.DoneWithError($"I couldn't update work item #{extracted.WorkItemId} - Azure DevOps returned: {ex.Message}");
        }
    }

    /// <summary>Returns the canonical-cased matching state name on success, or a ready-to-send
    /// rejection message (naming the valid options) when the requested state doesn't match. Returns
    /// (null, null) when there's nothing configured to validate against yet, or the item's type
    /// couldn't be resolved - in both cases the caller falls back to letting Azure DevOps itself
    /// accept or reject the state, exactly like before this feature existed.</summary>
    private async Task<(string? NormalizedState, string? RejectionMessage)> ValidateStateAsync(
        AdoConnectionContext connection, int id, string requestedState, CancellationToken cancellationToken)
    {
        var result = await WorkItemStateValidator.ValidateAsync(_adoClient, _stateConfigService, connection, id, requestedState, cancellationToken);
        if (!result.HasConfig || result.IsValid)
        {
            return (result.NormalizedState, null);
        }

        return (null, $"'{requestedState}' isn't a valid state for a {result.WorkItemType} here. Valid states are: {string.Join(", ", result.ValidStates!)}. Which would you like?");
    }
}
