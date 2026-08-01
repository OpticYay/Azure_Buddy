using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Intent;
using AzureBuddy.Core.Matching;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Routing.Flows;

/// <summary>
/// Ports the n8n "Search Parent -> Has Candidates? -> Resolve Parent -> Parent Resolved? -> Get Linked
/// Bugs -> Has Linked Bugs? -> Duplicate Check -> Duplicate Found? -> Build Bug Payload -> Create Bug"
/// chain. Falls through to the conversational agent at any point a human judgment call is needed:
/// no parent candidates, an ambiguous parent match, or a likely duplicate bug.
/// </summary>
public sealed class CreateBugFlow
{
    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;
    private readonly ILogger<CreateBugFlow> _logger;

    public CreateBugFlow(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor, ILogger<CreateBugFlow> logger)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
        _logger = logger;
    }

    public async Task<FlowResult> ExecuteAsync(ExtractedIntent extracted, CancellationToken cancellationToken = default)
    {
        var connection = _connectionAccessor.Require();

        var parentIds = await _adoClient.QueryWiqlAsync(
            connection,
            WiqlQueryBuilder.SearchByTitle(extracted.ParentSearchTerm),
            cancellationToken);

        if (parentIds.Count == 0)
        {
            return FlowResult.FallThroughToAgent();
        }

        var parentDetails = await _adoClient.GetWorkItemsAsync(
            connection,
            parentIds,
            new[] { AdoFields.Title, AdoFields.WorkItemType },
            cancellationToken);

        var candidates = parentDetails
            .Select(w => new ParentCandidate(w.Id, w.Title ?? string.Empty, w.WorkItemType ?? string.Empty))
            .ToList();

        var resolution = ParentResolver.Resolve(candidates, extracted.ParentSearchTerm);
        if (!resolution.Resolved)
        {
            return FlowResult.FallThroughToAgent();
        }

        var parentId = resolution.ParentId!.Value;

        var linkedBugIds = await _adoClient.QueryWiqlAsync(
            connection,
            WiqlQueryBuilder.ChildrenOf(parentId),
            cancellationToken);

        if (linkedBugIds.Count > 0)
        {
            var linkedBugs = await _adoClient.GetWorkItemsAsync(connection, linkedBugIds, new[] { AdoFields.Title }, cancellationToken);
            var existingBugs = linkedBugs.Select(b => new ExistingBug(b.Id, b.Title ?? string.Empty)).ToList();

            var duplicateCheck = DuplicateDetector.FindDuplicate($"[Bug] - {extracted.Title}", existingBugs);
            if (duplicateCheck.DuplicateFound)
            {
                // A likely duplicate exists - ask the user whether to proceed anyway, don't silently create.
                return FlowResult.FallThroughToAgent();
            }
        }

        var workItem = await CreateBugAsync(connection, extracted, parentId, cancellationToken);

        if (workItem.Id == 0)
        {
            return FlowResult.Done($"I couldn't create the bug automatically - Azure DevOps did not return a valid id.");
        }

        return FlowResult.Done($"Bug #{workItem.Id} created and linked to Parent #{parentId}.\nTitle: [Bug] - {extracted.Title}");
    }

    private async Task<WorkItem> CreateBugAsync(AdoConnectionContext connection, ExtractedIntent extracted, int parentId, CancellationToken cancellationToken)
    {
        var description = BuildDescriptionHtml(extracted);
        var title = $"[Bug] - {extracted.Title}";

        var ops = new List<JsonPatchOperation>
        {
            JsonPatchOperation.Add($"/fields/{AdoFields.Title}", title),
            JsonPatchOperation.Add($"/fields/{AdoFields.ReproSteps}", description),
            AdoRelationOps.ParentLink($"{connection.OrganizationUrl.TrimEnd('/')}/{connection.Project}/_apis/wit/workItems/{parentId}")
        };

        if (!string.IsNullOrEmpty(extracted.Priority)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.Priority}", extracted.Priority));
        if (!string.IsNullOrEmpty(extracted.Severity)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.Severity}", extracted.Severity));
        if (!string.IsNullOrEmpty(extracted.AreaPath)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.AreaPath}", extracted.AreaPath));
        if (!string.IsNullOrEmpty(extracted.IterationPath)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.IterationPath}", extracted.IterationPath));
        if (!string.IsNullOrEmpty(extracted.AssignedTo)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.AssignedTo}", extracted.AssignedTo));

        try
        {
            return await _adoClient.CreateWorkItemAsync(connection, "Bug", ops, cancellationToken);
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "Failed to create bug linked to parent {ParentId}", parentId);
            return new WorkItem();
        }
    }

    /// <summary>Mirrors the n8n "Build Bug Payload" Code node's HTML template exactly: no literal
    /// newlines, &lt;br&gt;/&lt;b&gt; tags only.</summary>
    private static string BuildDescriptionHtml(ExtractedIntent extracted)
    {
        var steps = extracted.ReproSteps.Count > 0
            ? string.Join("<br>", extracted.ReproSteps.Select((s, i) => $"{i + 1}. {s}"))
            : "Not provided";

        return
            $"<b>Bug description:</b> {extracted.Title}<br><br>" +
            $"<b>Steps to reproduce:</b><br>{steps}<br><br>" +
            $"<b>Expected result:</b> {(string.IsNullOrEmpty(extracted.ExpectedResult) ? "Not provided" : extracted.ExpectedResult)}<br><br>" +
            $"<b>Actual result:</b> {(string.IsNullOrEmpty(extracted.ActualResult) ? "Not provided" : extracted.ActualResult)}<br><br>" +
            $"<b>Evidence:</b> {(string.IsNullOrEmpty(extracted.Evidence) ? "Not provided" : extracted.Evidence)}<br><br>" +
            $"<b>Environment:</b> {(string.IsNullOrEmpty(extracted.Environment) ? "Not provided" : extracted.Environment)}";
    }
}
