using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Common;
using AzureBuddy.Core.WorkItemStates;

namespace AzureBuddy.Core.Agent;

/// <summary>
/// Ports the 7 ai_tool httpRequestTool nodes (Search Work Items, Create Bug, Get Linked Items tool,
/// Get Work Item Details, Update Work Item, Get My Work Items, Attach Evidence Link) into plain methods
/// the conversational agent can call, plus get_prioritized_work_items (this app's own addition, not an
/// n8n port). Each returns the raw Azure DevOps JSON response as a string, same as the n8n tool nodes
/// fed raw HTTP responses back to the model - the agent's system prompt already tells it how to read
/// that shape. Every call resolves the current user's ADO connection from
/// AdoConnectionContextAccessor (set once per request by the controller) rather than any global config.
/// WIQL query shapes come from WiqlQueryBuilder and relation-building from AdoRelationOps - shared with
/// the deterministic flows in Routing/Flows/*, which need the same queries/links independently.
/// </summary>
public sealed class AdoWorkItemToolset
{
    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;
    private readonly WorkItemStateConfigService _stateConfigService;

    public AdoWorkItemToolset(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor, WorkItemStateConfigService stateConfigService)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
        _stateConfigService = stateConfigService;
    }

    public async Task<string> SearchWorkItemsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var name = GetString(args, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            return JsonSerializer.Serialize(new { error = "No search phrase provided. Pass keyword(s) from the work item title as 'name'." });
        }

        try
        {
            var ids = await AdoWorkItemQueries.SearchIdsByTitleAsync(_adoClient, connection, name, ct);

            if (ids.Count == 0)
            {
                // A structured "found nothing" rather than a bare "[]". The model was reading the empty
                // array as though the call had failed and telling users "there was an error with the
                // search phrase", which sends them off rewording a query that worked fine and genuinely
                // had no matches.
                return JsonSerializer.Serialize(new
                {
                    found = 0,
                    searched = name,
                    message = $"No open work items in this project have a title matching '{name}'. The search itself succeeded - there are simply no matches. Suggest the user try a different keyword from the title, or check whether the item is closed."
                });
            }

            var items = await _adoClient.GetWorkItemsAsync(connection, ids, new[] { AdoFields.Title, AdoFields.WorkItemType }, ct);
            return SerializeItems(items);
        }
        catch (AdoApiException ex)
        {
            // Every other tool here already reports ADO failures this way. Search didn't, so a real API
            // failure surfaced through the agent loop's generic handler with no indication it came from
            // Azure DevOps - indistinguishable, to the model, from a bad search phrase.
            return JsonSerializer.Serialize(new { error = $"The Azure DevOps search request failed: {ex.Message}" });
        }
    }

    public async Task<string> CreateLinkedBugAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var title = GetString(args, "title");
        var description = GetString(args, "description");
        var parentId = TextUtils.DigitsOnly(GetString(args, "parent_id"));

        var ops = BugCreationRequestBuilder.BuildOps(
            connection, title, description, parentId,
            GetOptionalString(args, "priority"), GetOptionalString(args, "severity"),
            GetOptionalString(args, "area_path"), GetOptionalString(args, "iteration_path"), GetOptionalString(args, "assigned_to"));

        try
        {
            var created = await _adoClient.CreateWorkItemAsync(connection, "Bug", ops, ct);
            return JsonSerializer.Serialize(new { id = created.Id, title = created.Title });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public async Task<string> GetLinkedItemsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var parentId = TextUtils.DigitsOnly(GetString(args, "parent_id"));

        if (!int.TryParse(parentId, out var parentIdInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical parent_id provided." });
        }

        var ids = await _adoClient.QueryWiqlAsync(connection, WiqlQueryBuilder.ChildrenOf(parentIdInt), ct);
        return JsonSerializer.Serialize(ids);
    }

    public async Task<string> GetWorkItemDetailsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var idsRaw = GetString(args, "ids");
        var ids = Regex.Matches(idsRaw, @"\d+").Select(m => int.Parse(m.Value)).ToList();

        if (ids.Count == 0)
        {
            return "[]";
        }

        var items = await _adoClient.GetWorkItemsAsync(
            connection,
            ids,
            new[] { AdoFields.Title, AdoFields.State, AdoFields.Description, AdoFields.ReproSteps, AdoFields.Priority, AdoFields.StartDate, AdoFields.TargetDate, AdoFields.DueDate, AdoFields.FinishDate },
            ct);

        return JsonSerializer.Serialize(items.Select(i => new
        {
            id = i.Id,
            title = i.Title,
            state = i.State,
            description = i.Description,
            reproSteps = i.ReproSteps,
            priority = i.Priority,
            startDate = i.StartDate,
            dueDate = i.DueDate,
            overdue = WorkItemUrgencyRanker.IsOverdue(i)
        }));
    }

    public async Task<string> UpdateWorkItemAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var id = TextUtils.DigitsOnly(GetString(args, "id"));
        var state = GetOptionalString(args, "state");
        var comment = GetOptionalString(args, "comment");

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

        // Validate the requested state against the admin-configured list for THIS item's work item
        // type before ever calling Azure DevOps - see WorkItemStateConfiguration.cs for why this list
        // is backend-configured rather than read live from ADO. An empty configured list (nobody has
        // set one up for this type yet) means there's nothing to validate against, so the request
        // proceeds and ADO's own validation is the only gate, same as before this feature existed.
        if (!string.IsNullOrEmpty(state))
        {
            var result = await WorkItemStateValidator.ValidateAsync(_adoClient, _stateConfigService, connection, idInt, state, ct);
            if (result.HasConfig)
            {
                if (!result.IsValid)
                {
                    // Not an ADO failure - report it distinctly so the agent offers the valid list
                    // back to the user instead of treating this like a generic API error.
                    return JsonSerializer.Serialize(new
                    {
                        error = $"'{state}' isn't a valid state for a {result.WorkItemType} here.",
                        workItemType = result.WorkItemType,
                        validStates = result.ValidStates,
                        message = $"Tell the user '{state}' isn't valid for a {result.WorkItemType}, list the validStates, and ask which one they'd like - do not call this tool again until they answer."
                    });
                }
                state = result.NormalizedState;
            }
        }

        var ops = new List<JsonPatchOperation>();
        if (!string.IsNullOrEmpty(state)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.State}", state));
        if (!string.IsNullOrEmpty(comment)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.History}", comment));

        try
        {
            var updated = await _adoClient.UpdateWorkItemAsync(connection, idInt, ops, ct);
            return JsonSerializer.Serialize(new { id = updated.Id, state = updated.State });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public async Task<string> GetMyWorkItemsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var state = GetOptionalString(args, "state");
        var workItemType = GetOptionalString(args, "work_item_type");

        var items = await AdoWorkItemQueries.GetAssignedToMeAsync(_adoClient, connection, state, workItemType, sortByUrgency: false, ct);
        return items.Count == 0 ? "[]" : SerializeItemsWithUrgency(items);
    }

    /// <summary>Same data as get_my_work_items, but ranked most-to-least urgent by
    /// WorkItemUrgencyRanker (overdue first, then priority, then proximity to due date) instead of
    /// left in whatever order Azure DevOps returned them - use this instead of get_my_work_items when
    /// the user is asking what to work on first/next, not just for a plain list.</summary>
    public async Task<string> GetPrioritizedWorkItemsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var state = GetOptionalString(args, "state");
        var workItemType = GetOptionalString(args, "work_item_type");

        var ranked = await AdoWorkItemQueries.GetAssignedToMeAsync(_adoClient, connection, state, workItemType, sortByUrgency: true, ct);
        return ranked.Count == 0 ? "[]" : SerializeItemsWithUrgency(ranked);
    }

    public async Task<string> AttachEvidenceLinkAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var id = TextUtils.DigitsOnly(GetString(args, "id"));
        var evidenceUrl = GetString(args, "evidence_url");

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

        var ops = new List<JsonPatchOperation>
        {
            AdoRelationOps.EvidenceLink(evidenceUrl, "Evidence attached via QA Azure Buddy")
        };

        try
        {
            var updated = await _adoClient.UpdateWorkItemAsync(connection, idInt, ops, ct);
            return JsonSerializer.Serialize(new { id = updated.Id });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string SerializeItems(IReadOnlyList<WorkItem> items) =>
        JsonSerializer.Serialize(items.Select(i => new { id = i.Id, title = i.Title, type = i.WorkItemType, state = i.State }));

    private static string SerializeItemsWithUrgency(IReadOnlyList<WorkItem> items) =>
        JsonSerializer.Serialize(items.Select(i => new
        {
            id = i.Id,
            title = i.Title,
            type = i.WorkItemType,
            state = i.State,
            priority = i.Priority,
            startDate = i.StartDate,
            dueDate = i.DueDate,
            overdue = WorkItemUrgencyRanker.IsOverdue(i)
        }));

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) ? v.GetString() ?? string.Empty : string.Empty;

    private static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) ? v.GetString() : null;
}
