using AzureBuddy.Core.AzureDevOps;

namespace AzureBuddy.Tests.Integration;

/// <summary>
/// Test double for IAdoClient used by the integration tests - none of them should hit a real Azure
/// DevOps instance. Each method has a settable delegate defaulting to a canned success response, so
/// individual tests can override just the behavior they care about (e.g. make TestConnectionAsync
/// fail) without needing a full mocking framework.
/// </summary>
public sealed class FakeAdoClient : IAdoClient
{
    public Func<AdoConnectionContext, string, IReadOnlyList<int>> QueryWiqlBehavior { get; set; } = (_, _) => Array.Empty<int>();
    public Func<AdoConnectionContext, IEnumerable<int>, IReadOnlyList<string>, IReadOnlyList<WorkItem>> GetWorkItemsBehavior { get; set; } = (_, ids, _) => ids.Select(id => new WorkItem { Id = id }).ToList();
    public Func<AdoConnectionContext, string, IReadOnlyList<JsonPatchOperation>, WorkItem> CreateWorkItemBehavior { get; set; } = (_, _, _) => new WorkItem { Id = 1 };
    public Func<AdoConnectionContext, int, IReadOnlyList<JsonPatchOperation>, WorkItem> UpdateWorkItemBehavior { get; set; } = (_, id, _) => new WorkItem { Id = id };
    public Func<AdoConnectionContext, string, byte[], AdoAttachmentReference> CreateAttachmentBehavior { get; set; } =
        (_, fileName, _) => new AdoAttachmentReference("fake-attachment-id", $"https://fake.ado.local/attachments/{fileName}");
    public Func<AdoConnectionContext, bool> TestConnectionBehavior { get; set; } = _ => true;
    public Func<AdoConnectionContext, int, WorkItem?> GetWorkItemBehavior { get; set; } = (_, id) => new WorkItem { Id = id };
    public Func<AdoConnectionContext, string, IReadOnlyList<ResolvedIdentity>> SearchIdentitiesBehavior { get; set; } = (_, _) => Array.Empty<ResolvedIdentity>();

    public List<(string Method, AdoConnectionContext Connection)> Calls { get; } = new();

    public Task<IReadOnlyList<int>> QueryWiqlAsync(AdoConnectionContext connection, string wiqlQuery, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(QueryWiqlAsync), connection));
        return Task.FromResult(QueryWiqlBehavior(connection, wiqlQuery));
    }

    public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(AdoConnectionContext connection, IEnumerable<int> ids, IReadOnlyList<string> fields, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(GetWorkItemsAsync), connection));
        return Task.FromResult(GetWorkItemsBehavior(connection, ids, fields));
    }

    public Task<WorkItem> CreateWorkItemAsync(AdoConnectionContext connection, string workItemType, IReadOnlyList<JsonPatchOperation> operations, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(CreateWorkItemAsync), connection));
        return Task.FromResult(CreateWorkItemBehavior(connection, workItemType, operations));
    }

    public Task<WorkItem> UpdateWorkItemAsync(AdoConnectionContext connection, int id, IReadOnlyList<JsonPatchOperation> operations, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(UpdateWorkItemAsync), connection));
        return Task.FromResult(UpdateWorkItemBehavior(connection, id, operations));
    }

    public Task<AdoAttachmentReference> CreateAttachmentAsync(AdoConnectionContext connection, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(CreateAttachmentAsync), connection));
        return Task.FromResult(CreateAttachmentBehavior(connection, fileName, content));
    }

    public Task<bool> TestConnectionAsync(AdoConnectionContext connection, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(TestConnectionAsync), connection));
        return Task.FromResult(TestConnectionBehavior(connection));
    }

    public Task<WorkItem?> GetWorkItemAsync(AdoConnectionContext connection, int id, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(GetWorkItemAsync), connection));
        return Task.FromResult(GetWorkItemBehavior(connection, id));
    }

    public Task<IReadOnlyList<ResolvedIdentity>> SearchIdentitiesAsync(AdoConnectionContext connection, string filterValue, CancellationToken cancellationToken = default)
    {
        Calls.Add((nameof(SearchIdentitiesAsync), connection));
        return Task.FromResult(SearchIdentitiesBehavior(connection, filterValue));
    }

    /// <summary>Resets all behaviors to their defaults and clears the call log - call between tests
    /// that share a factory instance so one test's configuration can't leak into the next.</summary>
    public void Reset()
    {
        QueryWiqlBehavior = (_, _) => Array.Empty<int>();
        GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem { Id = id }).ToList();
        CreateWorkItemBehavior = (_, _, _) => new WorkItem { Id = 1 };
        UpdateWorkItemBehavior = (_, id, _) => new WorkItem { Id = id };
        CreateAttachmentBehavior = (_, fileName, _) => new AdoAttachmentReference("fake-attachment-id", $"https://fake.ado.local/attachments/{fileName}");
        TestConnectionBehavior = _ => true;
        GetWorkItemBehavior = (_, id) => new WorkItem { Id = id };
        SearchIdentitiesBehavior = (_, _) => Array.Empty<ResolvedIdentity>();
        Calls.Clear();
    }
}
