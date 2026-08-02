using System.Net;
using System.Text;
using AzureBuddy.Core.AzureDevOps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace AzureBuddy.Tests.AzureDevOps;

/// <summary>
/// Integration tests for AdoClient against a real HTTP server (WireMock.Net) rather than a mocked
/// HttpClient - this exercises the actual request construction (URLs, auth header, body encoding)
/// and response parsing end-to-end, the way a hand-mocked HttpMessageHandler wouldn't catch a bug in
/// e.g. BuildUrl's query-string-separator logic.
/// </summary>
public class AdoClientTests : IDisposable
{
    private const string Pat = "test-pat-value";
    private const string Project = "TestProject";

    private readonly WireMockServer _server;
    private readonly AdoClient _client;
    private readonly AdoConnectionContext _connection;

    public AdoClientTests()
    {
        _server = WireMockServer.Start();
        _connection = new AdoConnectionContext(_server.Url!, Project, Pat);

        var options = Options.Create(new AdoOptions { ApiVersion = "7.1" });
        _client = new AdoClient(new HttpClient(), options, NullLogger<AdoClient>.Instance);
    }

    public void Dispose() => _server.Stop();

    private static string ExpectedAuthHeader(string pat) =>
        "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));

    [Fact]
    public async Task QueryWiqlAsync_SendsAuthorizedPostAndParsesWorkItemIds()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/wiql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { workItems = new[] { new { id = 1 }, new { id = 2 } } }));

        var ids = await _client.QueryWiqlAsync(_connection, "SELECT [System.Id] FROM WorkItems");

        Assert.Equal(new[] { 1, 2 }, ids);

        var logEntry = Assert.Single(_server.LogEntries);
        Assert.Equal(ExpectedAuthHeader(Pat), logEntry.RequestMessage!.Headers!["Authorization"].Single());
        Assert.Contains("api-version=7.1", logEntry.RequestMessage!.Url!);
    }

    [Fact]
    public async Task GetWorkItemsAsync_EmptyIds_ReturnsEmptyWithoutCallingServer()
    {
        var items = await _client.GetWorkItemsAsync(_connection, Array.Empty<int>(), new[] { "System.Title" });

        Assert.Empty(items);
        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task GetWorkItemsAsync_ParsesFieldsFromResponse()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/workitems").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = new[]
                {
                    new { id = 101, fields = new Dictionary<string, object> { ["System.Title"] = "Login bug", ["System.State"] = "Active" } }
                }
            }));

        var items = await _client.GetWorkItemsAsync(_connection, new[] { 101 }, new[] { "System.Title", "System.State" });

        var item = Assert.Single(items);
        Assert.Equal(101, item.Id);
        Assert.Equal("Login bug", item.Title);
        Assert.Equal("Active", item.State);
    }

    [Fact]
    public async Task CreateWorkItemAsync_SendsJsonPatchContentTypeAndBody()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/workitems/$Bug").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = 555, fields = new Dictionary<string, object> { ["System.Title"] = "[Bug] - test" } }));

        var ops = new[] { JsonPatchOperation.Add("/fields/System.Title", "[Bug] - test") };
        var created = await _client.CreateWorkItemAsync(_connection, "Bug", ops);

        Assert.Equal(555, created.Id);

        var logEntry = Assert.Single(_server.LogEntries);
        Assert.StartsWith("application/json-patch+json", logEntry.RequestMessage!.Headers!["Content-Type"].Single());
        Assert.Contains("System.Title", logEntry.RequestMessage!.Body);
    }

    [Fact]
    public async Task UpdateWorkItemAsync_UsesPatchMethodOnCorrectId()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/workitems/42").UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = 42, fields = new Dictionary<string, object> { ["System.State"] = "Resolved" } }));

        var ops = new[] { JsonPatchOperation.Add("/fields/System.State", "Resolved") };
        var updated = await _client.UpdateWorkItemAsync(_connection, 42, ops);

        Assert.Equal(42, updated.Id);
        Assert.Equal("Resolved", updated.State);
    }

    [Fact]
    public async Task CreateAttachmentAsync_SendsRawBytesAndReturnsUrl()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/attachments").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = "abc-123",
                url = $"{_server.Url}/{Project}/_apis/wit/attachments/abc-123"
            }));

        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var attachment = await _client.CreateAttachmentAsync(_connection, "screenshot.png", bytes);

        Assert.Equal("abc-123", attachment.Id);
        Assert.Contains("attachments/abc-123", attachment.Url);

        var logEntry = Assert.Single(_server.LogEntries);
        Assert.Contains("fileName=screenshot.png", logEntry.RequestMessage!.Url!);
    }

    [Fact]
    public async Task TestConnectionAsync_SuccessResponse_ReturnsTrue()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/workitemtypes").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = Array.Empty<object>() }));

        Assert.True(await _client.TestConnectionAsync(_connection));
    }

    [Fact]
    public async Task TestConnectionAsync_UnauthorizedResponse_ReturnsFalse()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/workitemtypes").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        Assert.False(await _client.TestConnectionAsync(_connection));
    }

    [Fact]
    public async Task TestConnectionAsync_UnreachableHost_ThrowsRatherThanReturningFalse()
    {
        // Documents the exact gap the audit found: AdoClient.TestConnectionAsync doesn't route
        // through ReadOrThrowAsync (the only place that turns failures into AdoApiException), so a
        // network failure surfaces as a raw HttpRequestException/TaskCanceledException - callers
        // (UserAdoConfigService) must catch that broadly, which is what the audit fix added.
        // A dedicated short-timeout client keeps this test fast regardless of how the platform
        // handles a refused connection (immediate refusal vs. a silent connect timeout).
        using var shortTimeoutHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var shortTimeoutClient = new AdoClient(shortTimeoutHttpClient, Options.Create(new AdoOptions { ApiVersion = "7.1" }), NullLogger<AdoClient>.Instance);
        var unreachableConnection = new AdoConnectionContext("https://127.0.0.1:1", Project, Pat);

        await Assert.ThrowsAnyAsync<Exception>(() => shortTimeoutClient.TestConnectionAsync(unreachableConnection));
    }

    [Fact]
    public async Task QueryWiqlAsync_NonSuccessStatusCode_ThrowsAdoApiExceptionWithStatusAndBody()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/wiql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("Unauthorized: bad PAT"));

        var ex = await Assert.ThrowsAsync<AdoApiException>(() => _client.QueryWiqlAsync(_connection, "SELECT [System.Id] FROM WorkItems"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Unauthorized: bad PAT", ex.Message);
    }

    [Fact]
    public async Task QueryWiqlAsync_MalformedJsonResponse_ThrowsAdoApiException()
    {
        _server
            .Given(Request.Create().WithPath($"/{Project}/_apis/wit/wiql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("not valid json"));

        await Assert.ThrowsAsync<AdoApiException>(() => _client.QueryWiqlAsync(_connection, "SELECT [System.Id] FROM WorkItems"));
    }
}
