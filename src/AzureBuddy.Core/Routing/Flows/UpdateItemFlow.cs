using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>Ports "Has Update Fields? -> Update Work Item (Code Path) -> Build Update Message".</summary>
public sealed class UpdateItemFlow
{
    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;
    private readonly ILogger<UpdateItemFlow> _logger;

    public UpdateItemFlow(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor, ILogger<UpdateItemFlow> logger)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
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

        var ops = new List<JsonPatchOperation>();
        if (hasState) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.State}", extracted.State));
        if (hasComment) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.History}", extracted.Comment));

        if (!int.TryParse(extracted.WorkItemId, out var id))
        {
            return FlowResult.FallThroughToAgent();
        }

        var connection = _connectionAccessor.Require();

        try
        {
            var updated = await _adoClient.UpdateWorkItemAsync(connection, id, ops, cancellationToken);

            var parts = new List<string> { $"Work item #{updated.Id} updated." };
            if (hasState) parts.Add($"New state: {extracted.State}.");
            if (hasComment) parts.Add("Comment added.");
            return FlowResult.Done(string.Join(" ", parts));
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Failed to update work item {Id}", id);
            return FlowResult.Done($"I couldn't update work item #{extracted.WorkItemId} - Azure DevOps returned: {ex.Message}");
        }
    }
}
