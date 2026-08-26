using System.Net;
using System.Text.Json;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Tests.Integration;
using Xunit;

namespace AzureBuddy.Tests.AzureDevOps;

public class AdoIdentityResolverTests
{
    private static readonly AdoConnectionContext Connection = new("https://dev.azure.com/org", "Project", "pat");

    private static AdoIdentityResolver NewResolver(FakeAdoClient client) => new(client);

    [Fact]
    public async Task ResolveAsync_ExactDisplayNameMatch_Resolves()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => new[]
            {
                new ResolvedIdentity("1", "Jane Doe", "jane@example.com"),
                new ResolvedIdentity("2", "John Smith", "john@example.com"),
            }
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.True(result.Resolved);
        Assert.Equal("jane@example.com", result.Identity!.UniqueName);
    }

    [Fact]
    public async Task ResolveAsync_SubstringMatch_Resolves()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => new[]
            {
                new ResolvedIdentity("1", "Jane Doe", "jane@example.com"),
            }
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane");

        Assert.True(result.Resolved);
        Assert.Equal("jane@example.com", result.Identity!.UniqueName);
    }

    [Fact]
    public async Task ResolveAsync_AmbiguousCandidatesWithinMargin_DoesNotResolve()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => new[]
            {
                new ResolvedIdentity("1", "Jane Doe", "jane.doe@example.com"),
                new ResolvedIdentity("2", "Jane Dean", "jane.dean@example.com"),
            }
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane");

        Assert.False(result.Resolved);
        Assert.Null(result.Identity);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task ResolveAsync_NoCandidates_DoesNotResolve()
    {
        var client = new FakeAdoClient { SearchIdentitiesBehavior = (_, _) => Array.Empty<ResolvedIdentity>() };

        var result = await NewResolver(client).ResolveAsync(Connection, "nobody");

        Assert.False(result.Resolved);
        Assert.Null(result.Identity);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_LowOverlapBelowThreshold_DoesNotResolve()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => new[] { new ResolvedIdentity("1", "Completely unrelated", "x@example.com") }
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.False(result.Resolved);
    }

    [Fact]
    public async Task ResolveAsync_SearchIdentitiesThrowsUnauthorized_FallsBackToWiqlSearch()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("no scope", statusCode: HttpStatusCode.Unauthorized),
            QueryWiqlBehavior = (_, _) => new[] { 1 },
            GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?>
                {
                    [AdoFields.AssignedTo] = MakeIdentityJson("Jane Doe", "jane@example.com"),
                }
            }).ToList()
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.True(result.Resolved);
        Assert.Equal("jane@example.com", result.Identity!.UniqueName);
        Assert.Contains(client.Calls, c => c.Method == nameof(IAdoClient.QueryWiqlAsync));
    }

    [Fact]
    public async Task ResolveAsync_SearchIdentitiesThrowsForbidden_FallsBackToWiqlSearch()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("no scope", statusCode: HttpStatusCode.Forbidden),
            QueryWiqlBehavior = (_, _) => new[] { 1 },
            GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?>
                {
                    [AdoFields.AssignedTo] = MakeIdentityJson("Jane Doe", "jane@example.com"),
                }
            }).ToList()
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.True(result.Resolved);
    }

    [Fact]
    public async Task ResolveAsync_SearchIdentitiesThrowsOtherStatusCode_PropagatesException()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("boom", statusCode: HttpStatusCode.InternalServerError),
        };

        await Assert.ThrowsAsync<AdoApiException>(() => NewResolver(client).ResolveAsync(Connection, "Jane Doe"));
    }

    [Fact]
    public async Task ResolveAsync_WiqlFallbackWithNoMatchingWorkItems_DoesNotResolve()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("no scope", statusCode: HttpStatusCode.Unauthorized),
            QueryWiqlBehavior = (_, _) => Array.Empty<int>(),
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.False(result.Resolved);
        Assert.Empty(result.Candidates);
        Assert.DoesNotContain(client.Calls, c => c.Method == nameof(IAdoClient.GetWorkItemsAsync));
    }

    [Fact]
    public async Task ResolveAsync_WiqlFallbackDeduplicatesRepeatedAssignees()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("no scope", statusCode: HttpStatusCode.Unauthorized),
            QueryWiqlBehavior = (_, _) => new[] { 1, 2 },
            GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?>
                {
                    [AdoFields.AssignedTo] = MakeIdentityJson("Jane Doe", "jane@example.com"),
                }
            }).ToList()
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.True(result.Resolved);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task ResolveAsync_WiqlFallbackAssignedToAsPlainString_ParsesDisplayName()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("no scope", statusCode: HttpStatusCode.Unauthorized),
            QueryWiqlBehavior = (_, _) => new[] { 1 },
            GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem
            {
                Id = id,
                Fields = new Dictionary<string, object?>
                {
                    [AdoFields.AssignedTo] = JsonDocument.Parse("\"Jane Doe\"").RootElement,
                }
            }).ToList()
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.True(result.Resolved);
        Assert.Equal("Jane Doe", result.Identity!.UniqueName);
    }

    [Fact]
    public async Task ResolveAsync_WiqlFallbackWorkItemMissingAssignedToField_IsSkipped()
    {
        var client = new FakeAdoClient
        {
            SearchIdentitiesBehavior = (_, _) => throw new AdoApiException("no scope", statusCode: HttpStatusCode.Unauthorized),
            QueryWiqlBehavior = (_, _) => new[] { 1 },
            GetWorkItemsBehavior = (_, ids, _) => ids.Select(id => new WorkItem { Id = id }).ToList()
        };

        var result = await NewResolver(client).ResolveAsync(Connection, "Jane Doe");

        Assert.False(result.Resolved);
        Assert.Empty(result.Candidates);
    }

    private static JsonElement MakeIdentityJson(string displayName, string uniqueName)
    {
        var json = JsonSerializer.Serialize(new { id = "1", displayName, uniqueName });
        return JsonDocument.Parse(json).RootElement;
    }
}
