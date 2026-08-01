using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBuddy.Core.AzureDevOps;

namespace AzureBuddy.Core.Agent;

/// <summary>
/// Ports the 7 ai_tool httpRequestTool nodes (Search Work Items, Create Bug, Get Linked Items tool,
/// Get Work Item Details, Update Work Item, Get My Work Items, Attach Evidence Link) into plain methods
/// the conversational agent can call. Each returns the raw Azure DevOps JSON response as a string, same
/// as the n8n tool nodes fed raw HTTP responses back to the model - the agent's system prompt already
/// tells it how to read that shape. Every call resolves the current user's ADO connection from
/// AdoConnectionContextAccessor (set once per request by the controller) rather than any global config.
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
        var ids = await _adoClient.QueryWiqlAsync(
            connection,
            $"SELECT [System.Id], [System.Title], [System.WorkItemType] FROM WorkItems WHERE [System.Title] CONTAINS '{EscapeWiql(name)}' AND [System.State] <> 'Closed' ORDER BY [System.CreatedDate] DESC",
            ct);

        if (ids.Count == 0)
        {
            return "[]";
        }

        var items = await _adoClient.GetWorkItemsAsync(connection, ids, new[] { AdoFields.Title, AdoFields.WorkItemType }, ct);
        return SerializeItems(items);
    }

    public async Task<string> CreateLinkedBugAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var title = GetString(args, "title");
        var description = GetString(args, "description");
        var parentId = DigitsOnly(GetString(args, "parent_id"));

        var ops = new List<JsonPatchOperation>
        {
            JsonPatchOperation.Add($"/fields/{AdoFields.Title}", title),
            JsonPatchOperation.Add($"/fields/{AdoFields.ReproSteps}", description),
            JsonPatchOperation.Add("/relations/-", new
            {
                rel = "System.LinkTypes.Hierarchy-Reverse",
                url = $"{connection.OrganizationUrl.TrimEnd('/')}/{connection.Project}/_apis/wit/workItems/{parentId}"
            })
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
        var parentId = DigitsOnly(GetString(args, "parent_id"));
        var ids = await _adoClient.QueryWiqlAsync(
            connection,
            $"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State], [System.CreatedDate] FROM WorkItems WHERE [System.Parent] = {parentId} ORDER BY [System.CreatedDate] DESC",
            ct);

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
        var id = DigitsOnly(GetString(args, "id"));
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
        var stateFilter = string.IsNullOrEmpty(state)
            ? "AND [System.State] <> 'Closed'"
            : $"AND [System.State] = '{EscapeWiql(state)}'";

        var ids = await _adoClient.QueryWiqlAsync(
            connection,
            $"SELECT [System.Id], [System.Title], [System.WorkItemType], [System.State] FROM WorkItems WHERE [System.AssignedTo] = @Me {stateFilter} ORDER BY [System.ChangedDate] DESC",
            ct);

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
        var id = DigitsOnly(GetString(args, "id"));
        var evidenceUrl = GetString(args, "evidence_url");

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

        var ops = new List<JsonPatchOperation>
        {
            JsonPatchOperation.Add("/relations/-", new
            {
                rel = "Hyperlink",
                url = evidenceUrl,
                attributes = new { comment = "Evidence attached via QA Azure Buddy" }
            })
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

    private static string DigitsOnly(string value) => Regex.Replace(value, "[^0-9]", "");

    private static string EscapeWiql(string value) => value.Replace("'", "''");
}
