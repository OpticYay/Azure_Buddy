using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Wraps the Azure DevOps work-items REST API (WIQL query, batch get, create, update, attachments).
/// Every call takes an explicit AdoConnectionContext (org URL, project, decrypted PAT) instead of a
/// fixed base address/credential baked in at startup, because this app now serves many users, each
/// with their own ADO org/project/PAT - there's no single "the" ADO connection anymore.
/// Retry-on-transient-failure is applied via a Polly policy registered on the HttpClient in DI
/// (2 tries, 1s wait - mirrors every httpRequest(Tool) node's retryOnFail/maxTries/waitBetweenTries
/// from the original n8n workflow this app replaced).
/// </summary>
public sealed class AdoClient : IAdoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string JsonPatchMediaType = "application/json-patch+json";

    // Named instead of inlined per-method so the API surface this client covers is visible at a
    // glance, and a typo in a path shows up as a compile-time-visible constant, not a buried literal.
    private static class Paths
    {
        public const string Wiql = "_apis/wit/wiql";
        public const string WorkItems = "_apis/wit/workitems";
        public const string Attachments = "_apis/wit/attachments";
        public const string WorkItemTypes = "_apis/wit/workitemtypes";
    }

    private readonly HttpClient _httpClient;
    private readonly AdoOptions _options;
    private readonly ILogger<AdoClient> _logger;

    public AdoClient(HttpClient httpClient, IOptions<AdoOptions> options, ILogger<AdoClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<int>> QueryWiqlAsync(AdoConnectionContext connection, string wiqlQuery, CancellationToken cancellationToken = default)
    {
        // The exact WIQL text is the single most useful thing to see when a query returns nothing or
        // 400s - the string is assembled from user-supplied fragments (search terms, states, ids), so
        // "what did we actually ask ADO" is not obvious from the call site. Debug rather than
        // Information because this fires on every ADO-backed turn. No secrets are in the query text.
        _logger.LogDebug("WIQL query against {Org}/{Project}: {Wiql}", connection.OrganizationUrl, connection.Project, wiqlQuery);

        var url = BuildUrl(connection, Paths.Wiql);
        var response = await SendAsync(connection, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new WiqlQueryRequest { Query = wiqlQuery }, options: JsonOptions)
        }, cancellationToken);

        var result = await ReadOrThrowAsync<WiqlQueryResponse>(response, cancellationToken);
        var ids = result.WorkItems.Select(w => w.Id).ToList();
        _logger.LogDebug("WIQL query returned {Count} work item(s).", ids.Count);
        return ids;
    }

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(
        AdoConnectionContext connection,
        IEnumerable<int> ids,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return Array.Empty<WorkItem>();
        }

        var url = BuildUrl(connection, $"{Paths.WorkItems}?ids={string.Join(",", idList)}&fields={string.Join(",", fields)}");
        var response = await SendAsync(connection, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        var result = await ReadOrThrowAsync<WorkItemsBatchResponse>(response, cancellationToken);
        return result.Value;
    }

    public async Task<WorkItem> CreateWorkItemAsync(
        AdoConnectionContext connection,
        string workItemType,
        IReadOnlyList<JsonPatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(connection, $"{Paths.WorkItems}/${workItemType}");
        var response = await SendJsonPatchAsync(connection, HttpMethod.Post, url, operations, cancellationToken);
        return await ReadOrThrowAsync<WorkItem>(response, cancellationToken);
    }

    public async Task<WorkItem> UpdateWorkItemAsync(
        AdoConnectionContext connection,
        int id,
        IReadOnlyList<JsonPatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(connection, $"{Paths.WorkItems}/{id}");
        var response = await SendJsonPatchAsync(connection, HttpMethod.Patch, url, operations, cancellationToken);
        return await ReadOrThrowAsync<WorkItem>(response, cancellationToken);
    }

    public async Task<AdoAttachmentReference> CreateAttachmentAsync(
        AdoConnectionContext connection,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(connection, $"{Paths.Attachments}?fileName={Uri.EscapeDataString(fileName)}");

        var response = await SendAsync(connection, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            // ADO's attachment upload takes the raw bytes as the request body, not multipart form data.
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
            }
        }, cancellationToken);

        var result = await ReadOrThrowAsync<AdoAttachmentResponse>(response, cancellationToken);
        return new AdoAttachmentReference(result.Id, result.Url);
    }

    public async Task<bool> TestConnectionAsync(AdoConnectionContext connection, CancellationToken cancellationToken = default)
    {
        // A minimal, side-effect-free call: listing work item types for the configured project only
        // succeeds if the org/project exist and the PAT has at least read access.
        var url = BuildUrl(connection, Paths.WorkItemTypes);
        using var response = await SendAsync(connection, () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private Task<HttpResponseMessage> SendJsonPatchAsync(
        AdoConnectionContext connection,
        HttpMethod method,
        string url,
        IReadOnlyList<JsonPatchOperation> operations,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(operations, JsonOptions);
        return SendAsync(connection, () => new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, JsonPatchMediaType)
        }, cancellationToken);
    }

    private Task<HttpResponseMessage> SendAsync(
        AdoConnectionContext connection,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var request = requestFactory();

        // Basic Auth with an empty username and the PAT as the password - Azure DevOps' documented PAT
        // convention. Built fresh per request (rather than a fixed DelegatingHandler) because the PAT
        // now varies by which user is making the call.
        var raw = Encoding.ASCII.GetBytes($":{connection.Pat}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));

        return _httpClient.SendAsync(request, cancellationToken);
    }

    private string BuildUrl(AdoConnectionContext connection, string relativePath)
    {
        var orgUrl = connection.OrganizationUrl.TrimEnd('/');
        var separator = relativePath.Contains('?') ? "&" : "?";
        return $"{orgUrl}/{connection.Project}/{relativePath}{separator}api-version={_options.ApiVersion}";
    }

    private async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately not logging the request's Authorization header (SendAsync never adds it
                // to any log-visible object) - only the URL/status/body, so PATs never end up in logs.
                // The body itself is truncated before it reaches either the log or the client-facing
                // exception message (surfaced verbatim by GlobalExceptionHandler as ado_api_error) -
                // ADO error bodies can include project/org metadata.
                var truncated = Truncate(body);
                _logger.LogWarning("Azure DevOps call to {Url} returned {Status}: {Body}", response.RequestMessage?.RequestUri, response.StatusCode, truncated);
                throw new AdoApiException($"Azure DevOps returned {(int)response.StatusCode}: {truncated}");
            }

            try
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions)
                       ?? throw new AdoApiException("Azure DevOps returned an empty response body.");
            }
            catch (JsonException ex)
            {
                throw new AdoApiException($"Failed to parse Azure DevOps response: {Truncate(body)}", ex);
            }
        }
    }

    private static string Truncate(string body) => body.Length > 500 ? body[..500] + "... [truncated]" : body;
}

/// <summary>Raised when an Azure DevOps call fails or returns an unparseable response, after retries
/// are exhausted. Callers mirror the n8n onError:"continueRegularOutput" behaviour by catching this
/// and reporting the actual error to the user rather than claiming success.</summary>
public sealed class AdoApiException : Exception
{
    public AdoApiException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
