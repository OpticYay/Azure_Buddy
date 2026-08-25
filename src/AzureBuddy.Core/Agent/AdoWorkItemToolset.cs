using System.Text.Json;
using System.Text.RegularExpressions;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
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
    /// <summary>Fields update_work_item_fields must never touch - identity/system bookkeeping that
    /// either Azure DevOps computes itself (Id, Rev) or that would move the item out of the context this
    /// tool operates in (TeamProject, WorkItemType).</summary>
    private static readonly HashSet<string> ImmutableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Rev", "System.TeamProject", "System.WorkItemType"
    };

    private readonly IAdoClient _adoClient;
    private readonly AdoConnectionContextAccessor _connectionAccessor;
    private readonly WorkItemStateConfigService _stateConfigService;
    private readonly AdoIdentityResolver _identityResolver;
    private readonly IPendingAttachmentStore _pendingAttachmentStore;
    private readonly ChatSessionContextAccessor _sessionAccessor;
    private readonly IAdoAttachmentService _attachmentService;

    public AdoWorkItemToolset(
        IAdoClient adoClient,
        AdoConnectionContextAccessor connectionAccessor,
        WorkItemStateConfigService stateConfigService,
        AdoIdentityResolver identityResolver,
        IPendingAttachmentStore pendingAttachmentStore,
        ChatSessionContextAccessor sessionAccessor,
        IAdoAttachmentService attachmentService)
    {
        _adoClient = adoClient;
        _connectionAccessor = connectionAccessor;
        _stateConfigService = stateConfigService;
        _identityResolver = identityResolver;
        _pendingAttachmentStore = pendingAttachmentStore;
        _sessionAccessor = sessionAccessor;
        _attachmentService = attachmentService;
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

    /// <summary>Types this tool will create - deliberately excludes "Bug", which keeps its own dedicated
    /// tool/template (CreateLinkedBugAsync) with the repro-steps HTML format and Severity field bugs need
    /// and these types don't.</summary>
    private static readonly HashSet<string> CreatableWorkItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Task", "User Story", "Feature"
    };

    public async Task<string> CreateWorkItemAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var type = GetString(args, "type");

        if (!CreatableWorkItemTypes.Contains(type))
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Unsupported work item type '{type}'. Use create_linked_bug for Bugs, or one of Task, User Story, Feature here."
            });
        }

        var title = GetString(args, "title");
        var description = GetString(args, "description");
        var parentId = GetOptionalString(args, "parent_id");

        var ops = new List<JsonPatchOperation>
        {
            JsonPatchOperation.Add($"/fields/{AdoFields.Title}", title),
            JsonPatchOperation.Add($"/fields/{AdoFields.Description}", description)
        };

        if (!string.IsNullOrEmpty(parentId))
        {
            ops.Add(AdoRelationOps.ParentLink(BugCreationRequestBuilder.ParentWorkItemUrl(connection, parentId)));
        }

        AddOptionalField(ops, AdoFields.Priority, GetOptionalString(args, "priority"));
        AddOptionalField(ops, AdoFields.AreaPath, GetOptionalString(args, "area_path"));
        AddOptionalField(ops, AdoFields.IterationPath, GetOptionalString(args, "iteration_path"));
        AddOptionalField(ops, AdoFields.AssignedTo, GetOptionalString(args, "assigned_to"));

        try
        {
            var created = await _adoClient.CreateWorkItemAsync(connection, type, ops, ct);
            return JsonSerializer.Serialize(new { id = created.Id, title = created.Title, type });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static void AddOptionalField(List<JsonPatchOperation> ops, string field, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ops.Add(JsonPatchOperation.Add($"/fields/{field}", value));
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

    /// <summary>Runs a model-authored WIQL WHERE-clause fragment, always composed inside a
    /// server-controlled SELECT/FROM/project-scope/ORDER BY - see WiqlFragmentCompiler for why the model
    /// is never allowed to author the whole query.</summary>
    public async Task<string> QueryWorkItemsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var fragment = GetString(args, "where_clause");

        var compiled = WiqlFragmentCompiler.Compile(fragment);
        if (!compiled.Success)
        {
            return JsonSerializer.Serialize(new { error = compiled.Error });
        }

        try
        {
            var ids = await _adoClient.QueryWiqlAsync(connection, compiled.Query!, ct);
            if (ids.Count == 0)
            {
                return JsonSerializer.Serialize(new { found = 0, message = "The query succeeded but matched no work items." });
            }

            var limited = ids.Take(200).ToList();
            var items = await _adoClient.GetWorkItemsAsync(connection, limited, new[] { AdoFields.Title, AdoFields.WorkItemType, AdoFields.State }, ct);
            return SerializeItems(items);
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = $"The Azure DevOps query failed: {ex.Message}" });
        }
    }

    /// <summary>Turns a person's name/email into the exact identity Azure DevOps needs written into
    /// fields like AssignedTo - see AdoIdentityResolver for the search/WIQL-fallback/scoring
    /// details.</summary>
    public async Task<string> ResolveIdentityAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var name = GetString(args, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            return JsonSerializer.Serialize(new { error = "No name provided to resolve." });
        }

        try
        {
            var resolution = await _identityResolver.ResolveAsync(connection, name, ct);
            if (resolution.Resolved)
            {
                var identity = resolution.Identity!;
                return JsonSerializer.Serialize(new { resolved = true, uniqueName = identity.UniqueName, displayName = identity.DisplayName });
            }

            if (resolution.Candidates.Count == 0)
            {
                return JsonSerializer.Serialize(new { resolved = false, message = $"No identity matching '{name}' was found. Ask the user to confirm the name or provide an email." });
            }

            return JsonSerializer.Serialize(new
            {
                resolved = false,
                candidates = resolution.Candidates.Select(c => new { c.Identity.DisplayName, c.Identity.UniqueName, c.Score }),
                message = "More than one identity could match - ask the user which one they mean before proceeding."
            });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Full read of a single work item, including its relations - the only tool that returns
    /// links, since get_work_item_details deliberately doesn't (see that method's field list).</summary>
    public async Task<string> GetWorkItemFullAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var id = TextUtils.DigitsOnly(GetString(args, "id"));

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

        try
        {
            var item = await _adoClient.GetWorkItemAsync(connection, idInt, ct);
            if (item is null)
            {
                return JsonSerializer.Serialize(new { error = $"Work item {idInt} was not found." });
            }

            return JsonSerializer.Serialize(new
            {
                id = item.Id,
                fields = item.Fields,
                relations = item.Relations?.Select(r => new
                {
                    rel = r.Rel,
                    url = r.Url,
                    targetWorkItemId = WorkItemRelation.TargetWorkItemId(r),
                    comment = r.Attributes?.Comment
                })
            });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Sets arbitrary work item fields by their ADO reference name (e.g. System.Title,
    /// Custom.MyField) - unlike update_work_item, which only knows about state/comment. Field names are
    /// allowlisted (System.*/Microsoft.VSTS.*/Custom.*) and immutable identity fields are rejected
    /// outright; System.State is still routed through WorkItemStateValidator so this can't bypass the
    /// admin-configured valid-states check update_work_item already enforces.</summary>
    public async Task<string> UpdateWorkItemFieldsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var id = TextUtils.DigitsOnly(GetString(args, "id"));

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

        if (!args.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.Serialize(new { error = "No 'fields' object provided - pass an object mapping ADO field reference names to their new values." });
        }

        var ops = new List<JsonPatchOperation>();
        foreach (var property in fieldsElement.EnumerateObject())
        {
            var fieldName = property.Name;

            if (ImmutableFields.Contains(fieldName))
            {
                return JsonSerializer.Serialize(new { error = $"'{fieldName}' cannot be changed - it is a system-managed field." });
            }

            if (!Regex.IsMatch(fieldName, @"^(System|Microsoft\.VSTS\.[A-Za-z]+|Custom)\.[A-Za-z0-9_.]+$"))
            {
                return JsonSerializer.Serialize(new { error = $"'{fieldName}' is not a recognized Azure DevOps field reference name." });
            }

            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();

            if (string.Equals(fieldName, AdoFields.State, StringComparison.OrdinalIgnoreCase) && value is not null)
            {
                var result = await WorkItemStateValidator.ValidateAsync(_adoClient, _stateConfigService, connection, idInt, value, ct);
                if (result.HasConfig)
                {
                    if (!result.IsValid)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            error = $"'{value}' isn't a valid state for a {result.WorkItemType} here.",
                            workItemType = result.WorkItemType,
                            validStates = result.ValidStates,
                            message = $"Tell the user '{value}' isn't valid for a {result.WorkItemType}, list the validStates, and ask which one they'd like - do not call this tool again until they answer."
                        });
                    }
                    value = result.NormalizedState;
                }
            }

            ops.Add(JsonPatchOperation.Add($"/fields/{fieldName}", value ?? string.Empty));
        }

        if (ops.Count == 0)
        {
            return JsonSerializer.Serialize(new { error = "No fields provided to update." });
        }

        try
        {
            var updated = await _adoClient.UpdateWorkItemAsync(connection, idInt, ops, ct);
            return JsonSerializer.Serialize(new { id = updated.Id, fields = updated.Fields });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Creates a link between two existing work items using one of AdoRelationOps'
    /// allowlisted friendly link-type names - unlike CreateLinkedBugAsync's ParentLink, this works on
    /// two already-existing items and supports every link type the allowlist covers.</summary>
    public async Task<string> LinkWorkItemsAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var sourceId = TextUtils.DigitsOnly(GetString(args, "source_id"));
        var targetId = TextUtils.DigitsOnly(GetString(args, "target_id"));
        var linkType = GetString(args, "link_type");
        var comment = GetOptionalString(args, "comment");

        if (!int.TryParse(sourceId, out var sourceIdInt) || !int.TryParse(targetId, out var targetIdInt))
        {
            return JsonSerializer.Serialize(new { error = "Both source_id and target_id must be valid numerical ids." });
        }

        if (!AdoRelationOps.LinkTypesByFriendlyName.TryGetValue(linkType, out var rel))
        {
            return JsonSerializer.Serialize(new
            {
                error = $"'{linkType}' is not a supported link type.",
                supportedLinkTypes = AdoRelationOps.LinkTypesByFriendlyName.Keys
            });
        }

        var targetUrl = BugCreationRequestBuilder.ParentWorkItemUrl(connection, targetIdInt.ToString());
        var ops = new List<JsonPatchOperation> { AdoRelationOps.WorkItemLink(rel, targetUrl, comment) };

        try
        {
            var updated = await _adoClient.UpdateWorkItemAsync(connection, sourceIdInt, ops, ct);
            return JsonSerializer.Serialize(new { id = updated.Id, linked = targetIdInt, linkType });
        }
        catch (AdoApiException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>Attaches whatever file the user most recently uploaded through the chat composer (held in
    /// IPendingAttachmentStore, keyed by the current session) to the given work item, then clears the
    /// pending slot - this is the only tool that reaches into session state rather than its own
    /// arguments, since the model never sees the raw file bytes.</summary>
    public async Task<string> AttachFileToWorkItemAsync(JsonElement args, CancellationToken ct)
    {
        var connection = _connectionAccessor.Require();
        var id = TextUtils.DigitsOnly(GetString(args, "id"));
        var comment = GetOptionalString(args, "comment") ?? "File attached via chat";

        if (!int.TryParse(id, out var idInt))
        {
            return JsonSerializer.Serialize(new { error = "No valid numerical id provided." });
        }

        var sessionId = _sessionAccessor.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            return JsonSerializer.Serialize(new { error = "No active chat session to look up an attachment for." });
        }

        var pending = await _pendingAttachmentStore.GetAsync(sessionId, ct);
        if (pending is null)
        {
            return JsonSerializer.Serialize(new { error = "No file has been uploaded in this conversation yet. Ask the user to attach one first." });
        }

        try
        {
            var attachmentUrl = await _attachmentService.AttachFileAsync(connection, idInt, pending.FileName, pending.Content, pending.ContentType, comment, ct);
            await _pendingAttachmentStore.RemoveAsync(sessionId, ct);
            return JsonSerializer.Serialize(new { id = idInt, fileName = pending.FileName, attachmentUrl });
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
