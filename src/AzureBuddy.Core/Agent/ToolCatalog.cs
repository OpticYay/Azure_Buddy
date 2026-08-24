using System.Text.Json;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Builds the AgentTool list, one per n8n ai_tool node. Descriptions started as verbatim ports
/// of each httpRequestTool node's toolDescription - the model leans on this exact wording - but they are
/// now maintained here rather than kept frozen: the n8n originals assumed a large hosted model, and the
/// thinner ones caused visible tool-selection failures on the self-hosted model this app can fall back to.</summary>
public sealed class ToolCatalog
{
    private readonly AdoWorkItemToolset _toolset;

    public ToolCatalog(AdoWorkItemToolset toolset)
    {
        _toolset = toolset;
    }

    public IReadOnlyList<AgentTool> GetTools() => new[]
    {
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "search_work_items",
                // Was "Search any workitem using name" - four words, against 1-3 explicit sentences on
                // every other tool. A small local model reading the catalog had almost nothing telling it
                // WHEN to reach for this, so a plain "find me the X story" request pattern-matched to bug
                // creation and invented a parent_id instead of looking one up.
                Description = "Use this tool to find work items by their title when the user refers to one by NAME rather than by numerical ID (e.g. 'the login page story', 'the SOA report task'). This is the ONLY way to turn a name into an id: call it before any tool that needs a numerical id, and never guess an id yourself. Provide one argument named name containing just the 1-2 most distinctive keywords from the title. Returns the matching work items with their ids, titles, and types; if nothing matches it returns found: 0, which means the search worked and there are genuinely no matches - report that plainly rather than inventing an item.",
                ParametersSchema = Schema(("name", "string", "Concise search phrase - plain keyword(s) from the work item title only. No WIQL, no JSON, no quotes."))
            },
            InvokeAsync = _toolset.SearchWorkItemsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "create_linked_bug",
                Description = "Use this tool to create a new Bug linked to a parent work item. Required: title, description (HTML-formatted per system rules), parent_id (verified numerical id). Optional (only include if the user explicitly specified them - never guess): priority (integer 1-4, 1=highest), severity (e.g. '1 - Critical', '2 - High', '3 - Medium', '4 - Low'), area_path, iteration_path, assigned_to (email or display name).",
                ParametersSchema = Schema(
                    required: new[] { "title", "description", "parent_id" },
                    ("title", "string", "Bug title, formatted as \"[Bug] - <Summary>\""),
                    ("description", "string", "HTML-formatted bug description per system prompt HTML rule (uses <br>/<b> tags, no literal newlines)"),
                    ("parent_id", "string", "Verified numerical id of the parent work item this bug links to. Never guess."),
                    ("priority", "string", "Optional integer 1-4 priority (1=highest). Leave blank if the user did not explicitly specify one."),
                    ("severity", "string", "Optional severity, e.g. '1 - Critical', '2 - High', '3 - Medium', '4 - Low'. Leave blank if the user did not explicitly specify one."),
                    ("area_path", "string", "Optional Area Path. Leave blank if the user did not explicitly specify one."),
                    ("iteration_path", "string", "Optional Iteration Path. Leave blank if the user did not explicitly specify one."),
                    ("assigned_to", "string", "Optional assignee email or display name. Leave blank if the user did not explicitly specify one."))
            },
            InvokeAsync = _toolset.CreateLinkedBugAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "get_linked_items",
                Description = "Use this tool to find all existing bugs that are linked to a specific User Story. You must provide one argument named parent_id, which is the numerical ID of the User Story. Always use the search_work_items tool first to find the parent_id if you do not already know it.",
                ParametersSchema = Schema(("parent_id", "string", "The numerical id of the parent work item (User Story/Task) to find linked bugs for. Must be a verified numeric id, never guessed."))
            },
            InvokeAsync = _toolset.GetLinkedItemsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "get_work_item_details",
                Description = "Use this tool to get the Title, State, Priority, Start Date, and Due Date of Azure DevOps work items (the response also includes an 'overdue' boolean, already computed). You must provide a single argument named ids, which must be a comma-separated list of numerical IDs (e.g., '12172,12171'). Always use this tool immediately after getting IDs from the get_linked_items tool so you can provide the user with the actual titles and statuses.",
                ParametersSchema = Schema(("ids", "string", "Comma-separated list of numerical work item IDs ONLY - digits and commas, nothing else, e.g. 12172,12171. Reformat cleanly even if the user wrote it with \"and\"/periods/other separators. Use the exact ids returned by get_linked_items - never guess."))
            },
            InvokeAsync = _toolset.GetWorkItemDetailsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "update_work_item",
                Description = "Use this tool to update an existing Azure DevOps work item (Bug, Task, User Story, etc). Provide 'id' (the numerical work item ID, required) and at least one of: 'state' (new state, e.g. 'Active', 'Resolved', 'Closed') or 'comment' (text to append to the work item's discussion/history). NEVER hallucinate an id - get it from search_work_items or get_linked_items tool first. If 'state' isn't one of this project's admin-configured valid states for that item's type, this returns an error with a validStates list instead of updating anything - relay that list to the user and ask which they want, don't retry with a guess.",
                ParametersSchema = Schema(
                    required: new[] { "id" },
                    ("id", "string", "Verified numerical work item id to update. Never guess."),
                    ("state", "string", "Optional new state, e.g. 'Active', 'Resolved', 'Closed'. Leave blank if not changing state."),
                    ("comment", "string", "Optional text to append to the work item history/discussion. Leave blank if not adding a comment."))
            },
            InvokeAsync = _toolset.UpdateWorkItemAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "get_my_work_items",
                Description = "Use this tool to list work items assigned to the current user (the account behind the configured Azure DevOps credential), including each item's Priority, Start Date, Due Date, and a computed 'overdue' flag. Optionally provide 'state' (e.g. 'Active', 'Resolved') to filter to one state; if omitted, returns all non-Closed items assigned to the user. Optionally provide 'work_item_type' (e.g. 'Bug', 'Task', 'User Story') when the user asked for one specific type - e.g. 'bugs assigned to me' MUST pass work_item_type='Bug', or the result will wrongly include every type. Returns items in whatever order Azure DevOps gave them - use get_prioritized_work_items instead if the user wants them ranked by urgency. Do not pass any other arguments.",
                ParametersSchema = Schema(
                    ("state", "string", "Optional single state to filter by, e.g. 'Active', 'Resolved'. Leave blank to get all non-Closed items."),
                    ("work_item_type", "string", "Optional single work item type to filter by, e.g. 'Bug', 'Task', 'User Story'. Leave blank to get every type - only set this when the user actually named a type."))
            },
            InvokeAsync = _toolset.GetMyWorkItemsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "get_prioritized_work_items",
                Description = "Use this tool when the user asks what to work on first/next, or which of their own assigned items are most urgent/overdue/pressing (e.g. 'what should I work on first', 'what's most urgent', 'prioritize my work items'). Returns the same assigned items as get_my_work_items (Priority, Start Date, Due Date, overdue flag) but already sorted most-to-least urgent: overdue items first regardless of priority, then by Priority number ascending (1=highest), then by how soon the due date is - items with no due date come last, ordered by priority among themselves. Present the results as a table in that order and don't re-sort them yourself. Optionally provide 'state' to filter to one status first, and/or 'work_item_type' (e.g. 'Bug') when the user asked to prioritize just one type.",
                ParametersSchema = Schema(
                    ("state", "string", "Optional single state to filter by, e.g. 'Active', 'Resolved'. Leave blank to get all non-Closed items."),
                    ("work_item_type", "string", "Optional single work item type to filter by, e.g. 'Bug', 'Task', 'User Story'. Leave blank to get every type - only set this when the user actually named a type."))
            },
            InvokeAsync = _toolset.GetPrioritizedWorkItemsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "attach_evidence_link",
                Description = "Use this tool to attach a piece of evidence to an existing work item as a hyperlink, when the user gives you a URL (e.g. a screenshot or log already hosted somewhere) rather than pasting the content inline. Provide 'id' (the numerical work item id) and 'evidence_url' (the URL to attach). This links to a URL - it does not upload binary files.",
                ParametersSchema = Schema(
                    required: new[] { "id", "evidence_url" },
                    ("id", "string", "Verified numerical work item id to attach evidence to. Never guess."),
                    ("evidence_url", "string", "The URL of the evidence (screenshot/log) to attach, exactly as provided by the user."))
            },
            InvokeAsync = _toolset.AttachEvidenceLinkAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "query_work_items",
                Description = "Use this tool for open-ended natural-language queries about work items that the other, more specific tools don't cover (e.g. 'bugs created last week', 'items assigned to jane with priority 1', 'active tasks in the Mobile area'). Provide one argument named where_clause containing ONLY a WIQL filter condition (the part that would go inside WHERE), using bracketed field reference names, e.g. \"[System.AssignedTo] = 'jane@example.com' AND [System.State] <> 'Closed'\". Do NOT include SELECT, FROM, project scope, or ORDER BY - those are added automatically and the project scope cannot be bypassed. Never include FROM/ORDER BY/MODE/ASOF anywhere in the fragment, even inside a string. Returns matching items (id, title, type, state), capped at 200 results.",
                ParametersSchema = Schema(
                    required: new[] { "where_clause" },
                    ("where_clause", "string", "A WIQL filter condition only, e.g. \"[System.State] = 'Active' AND [System.WorkItemType] = 'Bug'\". No SELECT/FROM/ORDER BY."))
            },
            InvokeAsync = _toolset.QueryWorkItemsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "resolve_identity",
                Description = "Use this tool to turn a person's name or partial name into the exact Azure DevOps identity (unique name/email) needed for fields like assigned_to, before calling update_work_item_fields or create_linked_bug with that value. Provide one argument named name (a display name, partial name, or email). If the result has resolved: true, use its uniqueName directly. If resolved: false with candidates, list the candidate display names for the user and ask which one they mean - do not guess. If resolved: false with no candidates, tell the user no match was found.",
                ParametersSchema = Schema(
                    required: new[] { "name" },
                    ("name", "string", "The person's name, partial name, or email as given by the user."))
            },
            InvokeAsync = _toolset.ResolveIdentityAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "get_work_item_full",
                Description = "Use this tool to get the COMPLETE data for a single work item, including every field and all of its relations/links (parent, children, related items, attachments) - use this instead of get_work_item_details whenever the user asks about a work item's links/relations, or wants a field get_work_item_details doesn't return. Provide one argument named id (a verified numerical id - never guess). Each relation in the response includes rel (link type), targetWorkItemId (when the link points at another work item), and any comment.",
                ParametersSchema = Schema(
                    required: new[] { "id" },
                    ("id", "string", "Verified numerical work item id. Never guess."))
            },
            InvokeAsync = _toolset.GetWorkItemFullAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "update_work_item_fields",
                Description = "Use this tool to set arbitrary Azure DevOps fields on an existing work item by their reference name (e.g. System.Title, System.AssignedTo, Microsoft.VSTS.Common.Priority, Custom.MyField) - use update_work_item instead when you only need to change state or add a comment, since that tool is simpler. Provide 'id' (verified numerical id) and 'fields' (an object mapping field reference names to their new values). System.Id, System.Rev, System.TeamProject, and System.WorkItemType cannot be changed. If 'fields' includes System.AssignedTo, resolve the person's identity with resolve_identity first and pass its uniqueName - do not pass a raw display name. If it includes System.State, an invalid value returns the project's validStates list instead of updating anything - relay that to the user rather than retrying with a guess.",
                ParametersSchema = Schema(
                    required: new[] { "id", "fields" },
                    ("id", "string", "Verified numerical work item id to update. Never guess."),
                    ("fields", "object", "Object mapping Azure DevOps field reference names to their new values, e.g. { \"System.Title\": \"New title\", \"Microsoft.VSTS.Common.Priority\": \"2\" }."))
            },
            InvokeAsync = _toolset.UpdateWorkItemFieldsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "link_work_items",
                Description = "Use this tool to create a link between two EXISTING work items (e.g. 'link 1234 as related to 5678', 'make 1234 a child of 5678'). Provide source_id (the item the link is added to), target_id (the item being linked to), and link_type - one of: parent, child, related, predecessor, successor, duplicate. Optionally provide comment. Both ids must be verified numerical ids - use search_work_items first if the user named an item rather than giving its id.",
                ParametersSchema = Schema(
                    required: new[] { "source_id", "target_id", "link_type" },
                    ("source_id", "string", "Verified numerical id of the work item the link is added to. Never guess."),
                    ("target_id", "string", "Verified numerical id of the work item being linked to. Never guess."),
                    ("link_type", "string", "One of: parent, child, related, predecessor, successor, duplicate."),
                    ("comment", "string", "Optional comment to attach to the link. Leave blank if the user didn't specify one."))
            },
            InvokeAsync = _toolset.LinkWorkItemsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "attach_file_to_work_item",
                Description = "Use this tool when the user has uploaded a file into this conversation (not given a URL - use attach_evidence_link for that) and wants it attached to a work item. Provide 'id' (verified numerical work item id) and optionally 'comment' (defaults to a generic note if omitted). This tool automatically finds whatever file the user most recently uploaded in this conversation - if none was uploaded, it returns an error and you should ask the user to attach a file first.",
                ParametersSchema = Schema(
                    required: new[] { "id" },
                    ("id", "string", "Verified numerical work item id to attach the uploaded file to. Never guess."),
                    ("comment", "string", "Optional comment describing the attachment. Leave blank for a generic default."))
            },
            InvokeAsync = _toolset.AttachFileToWorkItemAsync
        }
    };

    private static JsonElement Schema(params (string Name, string Type, string Description)[] properties) =>
        Schema(required: Array.Empty<string>(), properties);

    private static JsonElement Schema(string[] required, params (string Name, string Type, string Description)[] properties)
    {
        var propsDict = properties.ToDictionary(
            p => p.Name,
            p => new Dictionary<string, string> { ["type"] = p.Type, ["description"] = p.Description });

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = propsDict,
            ["required"] = required
        };

        var json = JsonSerializer.Serialize(schema);
        return JsonDocument.Parse(json).RootElement;
    }
}
