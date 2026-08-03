using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Common;

namespace AzureBuddy.Core.Agent;

/// <summary>
/// Ports the 7 ai_tool httpRequestTool nodes (Search Work Items, Create Bug, Get Linked Items tool,
/// Get Work Item Details, Update Work Item, Get My Work Items, Attach Evidence Link) into plain methods
/// the conversational agent can call. Each returns the raw Azure DevOps JSON response as a string, same
/// as the n8n tool nodes fed raw HTTP responses back to the model - the agent's system prompt already
/// tells it how to read that shape. Every call resolves the current user's ADO connection from
/// AdoConnectionContextAccessor (set once per request by the controller) rather than any global config.
/// WIQL query shapes come from WiqlQueryBuilder and relation-building from AdoRelationOps - shared with
/// the deterministic flows in Routing/Flows/*, which need the same queries/links independently.
/// </summary>
public sealed class AdoWorkItemToolset
{
    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;

    public AdoWorkItemToolset(IAdoClient adoClient, AdoConnectionContextAccessor connectionAccessor)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
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
            // Try the phrase as typed first - an exact substring hit is the most precise answer. Only if
            // that finds nothing do we widen to matching the words separately, so a confident phrase match
            // is never diluted by looser results.
            var ids = await _adoClient.QueryWiqlAsync(connection, WiqlQueryBuilder.SearchByTitle(name), ct);

            if (ids.Count == 0)
            {
                var words = WiqlQueryBuilder.SearchWords(name);
                if (words.Count > 0)
                {
                    ids = await _adoClient.QueryWiqlAsync(connection, WiqlQueryBuilder.SearchByTitleWords(words), ct);
                }
            }

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

        var ops = new List<JsonPatchOperation>
        {
            JsonPatchOperation.Add($"/fields/{AdoFields.Title}", title),
            JsonPatchOperation.Add($"/fields/{AdoFields.ReproSteps}", description),
            AdoRelationOps.ParentLink($"{connection.OrganizationUrl.TrimEnd('/')}/{connection.Project}/_apis/wit/workItems/{parentId}")
        };

        AddOptionalField(ops, args, "priority", AdoFields.Priority);
        AddOptionalField(ops, args, "severity", AdoFields.Severity);
        AddOptionalField(ops, args, "area_path", AdoFields.AreaPath);
        AddOptionalField(ops, args, "iteration_path", AdoFields.IterationPath);
        AddOptionalField(ops, args, "assigned_to", AdoFields.AssignedTo);

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
            new[] { AdoFields.Title, AdoFields.State, AdoFields.Description, AdoFields.ReproSteps },
            ct);

        return JsonSerializer.Serialize(items.Select(i => new
        {
            id = i.Id,
            title = i.Title,
            state = i.State,
            description = i.Description,
            reproSteps = i.ReproSteps
        }));
    }

    public async Task<string> UpdateWorkItemAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var id = TextUtils.DigitsOnly(GetString(args, "id"));
        var state = GetOptionalString(args, "state");
        var comment = GetOptionalString(args, "comment");

        var ops = new List<JsonPatchOperation>();
        if (!string.IsNullOrEmpty(state)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.State}", state));
        if (!string.IsNullOrEmpty(comment)) ops.Add(JsonPatchOperation.Add($"/fields/{AdoFields.History}", comment));

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

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

        var ids = await _adoClient.QueryWiqlAsync(connection, WiqlQueryBuilder.AssignedToMe(state), ct);

        if (ids.Count == 0)
        {
            return "[]";
        }

        var items = await _adoClient.GetWorkItemsAsync(connection, ids, new[] { AdoFields.Title, AdoFields.WorkItemType, AdoFields.State }, ct);
        return SerializeItems(items);
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

    private static void AddOptionalField(List<JsonPatchOperation> ops, JsonElement args, string argName, string field)
    {
        var value = GetOptionalString(args, argName);
        if (!string.IsNullOrEmpty(value))
        {
            ops.Add(JsonPatchOperation.Add($"/fields/{field}", value));
        }
    }

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) ? v.GetString() ?? string.Empty : string.Empty;

    private static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) ? v.GetString() : null;
}
