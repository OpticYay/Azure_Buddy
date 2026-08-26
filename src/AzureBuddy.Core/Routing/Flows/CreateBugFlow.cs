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
    /// <summary>Priority applied when the message signals urgency but the user never named a Priority
    /// field explicitly. "1" matches ADO's own convention of 1 = highest priority.</summary>
    private const string DefaultUrgentPriority = "1";

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

        // Two-stage search: try the phrase as typed first, and only if that finds nothing, widen to
        // matching each word independently - the extracted parent term is a paraphrase of the user's
        // sentence, so it often isn't a contiguous substring of the real title. Same widening the
        // agent's search tool does (see AdoWorkItemQueries.SearchIdsByTitleAsync).
        var parentIds = await AdoWorkItemQueries.SearchIdsByTitleAsync(_adoClient, connection, extracted.ParentSearchTerm, cancellationToken);

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
            return FlowResult.DoneWithError("I couldn't create the bug automatically - Azure DevOps did not return a valid id.");
        }

        // The user's own wording signaled urgency ("production down", "urgent", ...) but didn't state
        // an explicit Priority - default it to the highest priority so the bug doesn't quietly land at
        // whatever ADO's own default is, and say so, since this value was inferred rather than given.
        var urgencyNote = extracted.IsUrgent && string.IsNullOrEmpty(extracted.Priority)
            ? $" Flagged as urgent from your message, so I set Priority to {DefaultUrgentPriority}."
            : string.Empty;

        return FlowResult.DoneWithConfirmation(
            $"Bug #{workItem.Id} created and linked to Parent #{parentId}.\nTitle: [Bug] - {extracted.Title}{urgencyNote}",
            workItem.Id);
    }

    private async Task<WorkItem> CreateBugAsync(AdoConnectionContext connection, ExtractedIntent extracted, int parentId, CancellationToken cancellationToken)
    {
        var description = BuildDescriptionHtml(extracted);
        var title = $"[Bug] - {extracted.Title}";

        var priority = !string.IsNullOrEmpty(extracted.Priority)
            ? extracted.Priority
            : extracted.IsUrgent ? DefaultUrgentPriority : string.Empty;

        var ops = BugCreationRequestBuilder.BuildOps(
            connection, title, description, parentId.ToString(),
            priority, extracted.Severity, extracted.AreaPath, extracted.IterationPath, extracted.AssignedTo);

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

    /// <summary>Mirrors the n8n "Build Bug Payload" Code node's HTML template exactly - see
    /// <see cref="BugDescriptionTemplate"/> for the shared shape this must stay byte-compatible with.</summary>
    private static string BuildDescriptionHtml(ExtractedIntent extracted)
    {
        var steps = extracted.ReproSteps.Count > 0
            ? string.Join("<br>", extracted.ReproSteps.Select((s, i) => $"{i + 1}. {s}"))
            : BugDescriptionTemplate.NotProvided;

        return BugDescriptionTemplate.Build(
            extracted.Title, steps, extracted.ExpectedResult, extracted.ActualResult, extracted.Evidence, extracted.Environment);
    }
}
