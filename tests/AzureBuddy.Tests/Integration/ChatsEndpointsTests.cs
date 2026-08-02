using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.Chat;
using AzureBuddy.Core.Settings;
using AzureBuddy.Data.Entities;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of /api/chats/* - session/message CRUD, user-scoping (IDOR), and the
/// screenshot-to-ADO-attachment flow including its failure path.</summary>
public class ChatsEndpointsTests : IntegrationTestBase
{
    // The API now serializes enums (ChatMessageRole, ChatMessageType) as their string name rather than
    // the underlying int (see Program.cs's AddJsonOptions) - System.Net.Http.Json's parameterless
    // ReadFromJsonAsync/GetFromJsonAsync overloads don't know about that converter by default, so any
    // response containing a ChatMessageView needs to be read with these options explicitly instead.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public ChatsEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    private static MultipartFormDataContent BuildMessageForm(string role, string content, int? workItemId = null, (string FileName, byte[] Bytes)? screenshot = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(role), "role" },
            { new StringContent(content), "content" }
        };

        if (workItemId is not null)
        {
            form.Add(new StringContent(workItemId.Value.ToString()), "workItemId");
        }

        if (screenshot is not null)
        {
            form.Add(new ByteArrayContent(screenshot.Value.Bytes), "screenshot", screenshot.Value.FileName);
        }

        return form;
    }

    [Fact]
    public async Task CreateSession_DefaultsTitleWhenNoneGiven()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/chats", new CreateSessionRequest(null));
        var session = await response.Content.ReadFromJsonAsync<ChatSessionDetail>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("New conversation", session!.Title);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task ListSessions_ReturnsOnlyThisUsersSessions()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PostAsJsonAsync("/api/chats", new CreateSessionRequest("First"));
        await client.PostAsJsonAsync("/api/chats", new CreateSessionRequest("Second"));

        var page = await client.GetFromJsonAsync<PagedResult<ChatSessionSummary>>("/api/chats?page=1&pageSize=20");

        Assert.Equal(2, page!.TotalCount);
        Assert.Contains(page.Items, s => s.Title == "First");
        Assert.Contains(page.Items, s => s.Title == "Second");
    }

    [Fact]
    public async Task AppendTextMessage_ShowsUpInSessionHistory()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(client);

        using var form = BuildMessageForm("User", "Can you find the SOA report task?");
        var appendResponse = await client.PostAsync($"/api/chats/{session.Id}/messages", form);
        Assert.Equal(HttpStatusCode.OK, appendResponse.StatusCode);

        var detail = await client.GetFromJsonAsync<ChatSessionDetail>($"/api/chats/{session.Id}", JsonOptions);
        var message = Assert.Single(detail!.Messages);
        Assert.Equal(ChatMessageRole.User, message.Role);
        Assert.Equal("Can you find the SOA report task?", message.Content);
    }

    [Fact]
    public async Task AppendMessage_FirstUserMessage_BecomesSessionTitle()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(client);

        using var form = BuildMessageForm("User", "Create a bug for the login page");
        await client.PostAsync($"/api/chats/{session.Id}/messages", form);

        var updated = await client.GetFromJsonAsync<ChatSessionDetail>($"/api/chats/{session.Id}", JsonOptions);
        Assert.Equal("Create a bug for the login page", updated!.Title);
    }

    [Fact]
    public async Task AppendMessage_ToNonexistentSession_Returns404()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var form = BuildMessageForm("User", "hello");
        var response = await client.PostAsync($"/api/chats/{Guid.NewGuid()}/messages", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteSession_RemovesIt()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(client);

        var deleteResponse = await client.DeleteAsync($"/api/chats/{session.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/chats/{session.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task UserA_CannotReadUserB_ChatSession()
    {
        using var clientA = await CreateAuthenticatedClientAsync();
        var sessionA = await CreateSessionAsync(clientA);

        using var clientB = await CreateAuthenticatedClientAsync();
        var response = await clientB.GetAsync($"/api/chats/{sessionA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UserA_SessionDoesNotAppearInUserB_SessionList()
    {
        using var clientA = await CreateAuthenticatedClientAsync();
        await CreateSessionAsync(clientA, "User A's private session");

        using var clientB = await CreateAuthenticatedClientAsync();
        var page = await clientB.GetFromJsonAsync<PagedResult<ChatSessionSummary>>("/api/chats");

        Assert.DoesNotContain(page!.Items, s => s.Title == "User A's private session");
    }

    [Fact]
    public async Task UserB_CannotDeleteUserA_ChatSession()
    {
        using var clientA = await CreateAuthenticatedClientAsync();
        var sessionA = await CreateSessionAsync(clientA);

        using var clientB = await CreateAuthenticatedClientAsync();
        var deleteAttempt = await clientB.DeleteAsync($"/api/chats/{sessionA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deleteAttempt.StatusCode);

        // Still there from A's perspective - B's failed attempt had no effect.
        var stillThere = await clientA.GetAsync($"/api/chats/{sessionA.Id}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task UserB_CannotAppendMessageToUserA_ChatSession()
    {
        using var clientA = await CreateAuthenticatedClientAsync();
        var sessionA = await CreateSessionAsync(clientA);

        using var clientB = await CreateAuthenticatedClientAsync();
        using var form = BuildMessageForm("User", "injected by B");
        var response = await clientB.PostAsync($"/api/chats/{sessionA.Id}/messages", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AppendScreenshot_WithoutWorkItemId_ReturnsBadRequest()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(client);

        using var form = BuildMessageForm("User", "here's a screenshot", workItemId: null, screenshot: ("bug.png", new byte[] { 1, 2, 3 }));
        var response = await client.PostAsync($"/api/chats/{session.Id}/messages", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AppendScreenshot_AdoNotConfigured_ReturnsBadRequestWithClearMessage()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var session = await CreateSessionAsync(client);

        using var form = BuildMessageForm("User", "screenshot", workItemId: 42, screenshot: ("bug.png", new byte[] { 1, 2, 3 }));
        var response = await client.PostAsync($"/api/chats/{session.Id}/messages", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Azure DevOps", body);
    }

    [Fact]
    public async Task AppendScreenshot_AdoAcceptsUpload_StoresUrlNotBytes()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org", "Proj", "pat"));
        var session = await CreateSessionAsync(client);

        Factory.AdoClient.CreateAttachmentBehavior = (_, fileName, _) => new AdoAttachmentReference("att-1", $"https://dev.azure.com/org/Proj/_apis/wit/attachments/att-1?fileName={fileName}");

        using var form = BuildMessageForm("User", "here's what I see", workItemId: 42, screenshot: ("bug.png", new byte[] { 1, 2, 3, 4 }));
        var response = await client.PostAsync($"/api/chats/{session.Id}/messages", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AppendMessageResult>(JsonOptions);

        Assert.True(result!.Success);
        Assert.NotNull(result.Message.AdoAttachmentUrl);
        Assert.Contains("attachments/att-1", result.Message.AdoAttachmentUrl);
        Assert.Equal(42, result.Message.WorkItemId);
    }

    [Fact]
    public async Task AppendScreenshot_AdoUploadFails_RecordsFailureMessageInsteadOfSilentlyDropping()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org", "Proj", "pat"));
        var session = await CreateSessionAsync(client);

        Factory.AdoClient.CreateAttachmentBehavior = (_, _, _) => throw new AdoApiException("Azure DevOps returned 503: service unavailable");

        using var form = BuildMessageForm("User", "here's what I see", workItemId: 42, screenshot: ("bug.png", new byte[] { 1, 2, 3, 4 }));
        var response = await client.PostAsync($"/api/chats/{session.Id}/messages", form);

        // The HTTP request itself succeeds (a message got recorded) - the failure is represented in
        // the response body, not as an HTTP error status.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AppendMessageResult>(JsonOptions);

        Assert.False(result!.Success);
        Assert.Null(result.Message.AdoAttachmentUrl);
        Assert.Contains("couldn't attach", result.Message.Content, StringComparison.OrdinalIgnoreCase);

        // And the failure message really was persisted - not just returned and forgotten.
        var detail = await client.GetFromJsonAsync<ChatSessionDetail>($"/api/chats/{session.Id}", JsonOptions);
        var persisted = Assert.Single(detail!.Messages);
        Assert.Null(persisted.AdoAttachmentUrl);
    }

    private static async Task<ChatSessionDetail> CreateSessionAsync(HttpClient client, string? title = null)
    {
        var response = await client.PostAsJsonAsync("/api/chats", new CreateSessionRequest(title));
        return (await response.Content.ReadFromJsonAsync<ChatSessionDetail>(JsonOptions))!;
    }
}
