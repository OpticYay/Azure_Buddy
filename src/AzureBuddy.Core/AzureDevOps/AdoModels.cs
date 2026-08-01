using System.Text.Json.Serialization;

namespace AzureBuddy.Core.AzureDevOps;

public sealed class JsonPatchOperation
{
    [JsonPropertyName("op")]
    public string Op { get; init; } = "add";

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("value")]
    public required object Value { get; init; }

    public static JsonPatchOperation Add(string path, object value) => new() { Op = "add", Path = path, Value = value };
}

public sealed class WiqlQueryRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }
}

public sealed class WiqlQueryResponse
{
    [JsonPropertyName("workItems")]
    public List<WiqlWorkItemReference> WorkItems { get; init; } = new();
}

public sealed class WiqlWorkItemReference
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

public sealed class WorkItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("fields")]
    public Dictionary<string, object?> Fields { get; init; } = new();

    public string? Title => GetField("System.Title");
    public string? WorkItemType => GetField("System.WorkItemType");
    public string? State => GetField("System.State");
    public string? Description => GetField("System.Description");
    public string? ReproSteps => GetField("Microsoft.VSTS.TCM.ReproSteps");

    private string? GetField(string name) =>
        Fields.TryGetValue(name, out var value) ? value?.ToString() : null;
}

public sealed class WorkItemsBatchResponse
{
    [JsonPropertyName("value")]
    public List<WorkItem> Value { get; init; } = new();
}

public sealed record AdoAttachmentReference(string Id, string Url);

public sealed class AdoAttachmentResponse
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

/// <summary>Well-known Azure DevOps field reference names, so callers don't hand-type strings.</summary>
public static class AdoFields
{
    public const string Title = "System.Title";
    public const string WorkItemType = "System.WorkItemType";
    public const string State = "System.State";
    public const string Description = "System.Description";
    public const string ReproSteps = "Microsoft.VSTS.TCM.ReproSteps";
    public const string History = "System.History";
    public const string AreaPath = "System.AreaPath";
    public const string IterationPath = "System.IterationPath";
    public const string AssignedTo = "System.AssignedTo";
    public const string Priority = "Microsoft.VSTS.Common.Priority";
    public const string Severity = "Microsoft.VSTS.Common.Severity";
    public const string CreatedDate = "System.CreatedDate";
    public const string ChangedDate = "System.ChangedDate";
    public const string Parent = "System.Parent";
}
