using System.Text.Json;
using AzureBuddy.Core.Llm.Models;

namespace AzureBuddy.Core.Agent;

/// <summary>Builds the AgentTool list, one per n8n ai_tool node. Descriptions are ported verbatim from
/// each httpRequestTool node's toolDescription - the model leans on this exact wording.</summary>
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
                Description = "Search any workitem using name",
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
                Description = "Use this tool to get the Title and State of Azure DevOps work items. You must provide a single argument named ids, which must be a comma-separated list of numerical IDs (e.g., '12172,12171'). Always use this tool immediately after getting IDs from the get_linked_items tool so you can provide the user with the actual titles and statuses.",
                ParametersSchema = Schema(("ids", "string", "Comma-separated list of numerical work item IDs ONLY - digits and commas, nothing else, e.g. 12172,12171. Reformat cleanly even if the user wrote it with \"and\"/periods/other separators. Use the exact ids returned by get_linked_items - never guess."))
            },
            InvokeAsync = _toolset.GetWorkItemDetailsAsync
        },
        new AgentTool
        {
            Definition = new ToolDefinition
            {
                Name = "update_work_item",
                Description = "Use this tool to update an existing Azure DevOps work item (Bug, Task, User Story, etc). Provide 'id' (the numerical work item ID, required) and at least one of: 'state' (new state, e.g. 'Active', 'Resolved', 'Closed') or 'comment' (text to append to the work item's discussion/history). NEVER hallucinate an id - get it from search_work_items or get_linked_items tool first.",
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
                Description = "Use this tool to list work items assigned to the current user (the account behind the configured Azure DevOps credential). Optionally provide 'state' (e.g. 'Active', 'Resolved') to filter to one state; if omitted, returns all non-Closed items assigned to the user. Do not pass any other arguments.",
                ParametersSchema = Schema(("state", "string", "Optional single state to filter by, e.g. 'Active', 'Resolved'. Leave blank to get all non-Closed items."))
            },
            InvokeAsync = _toolset.GetMyWorkItemsAsync
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
