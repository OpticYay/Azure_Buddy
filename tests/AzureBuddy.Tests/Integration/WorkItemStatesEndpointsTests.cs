using System.Net;
using System.Net.Http.Json;
using AzureBuddy.Core.WorkItemStates;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of api/admin/work-item-states (Admin-only CRUD) and
/// api/work-item-states/{type} (read-only, any authenticated user) - the role gate on each, and that
/// disabling/deleting a state actually changes what the public read-only endpoint returns.</summary>
public class WorkItemStatesEndpointsTests : IntegrationTestBase
{
    public WorkItemStatesEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task AdminGet_AsNonAdmin_ReturnsForbidden()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/work-item-states");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminPost_AsNonAdmin_ReturnsForbidden()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/work-item-states",
            new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "New", DisplayOrder = 0, IsEnabled = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PublicGet_Unauthenticated_ReturnsUnauthorized()
    {
        var response = await Client.GetAsync("/api/work-item-states/Bug");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PublicGet_AsRegularAuthenticatedUser_Succeeds()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();
        await admin.PostAsJsonAsync(
            "/api/admin/work-item-states",
            new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "Active", DisplayOrder = 0, IsEnabled = true });

        using var regularUser = await CreateAuthenticatedClientAsync();
        var response = await regularUser.GetAsync("/api/work-item-states/Bug");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var states = await response.Content.ReadFromJsonAsync<List<string>>();
        Assert.Contains("Active", states!);
    }

    [Fact]
    public async Task PublicGet_UnconfiguredType_ReturnsEmptyList()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var states = await client.GetFromJsonAsync<List<string>>("/api/work-item-states/SomeTypeNobodyConfigured");

        Assert.NotNull(states);
        Assert.Empty(states!);
    }

    [Fact]
    public async Task AdminCreate_ThenPublicGet_ReturnsItOrderedByDisplayOrder()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();
        await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Task", StateName = "Done", DisplayOrder = 1, IsEnabled = true });
        await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Task", StateName = "To Do", DisplayOrder = 0, IsEnabled = true });

        var states = await admin.GetFromJsonAsync<List<string>>("/api/work-item-states/Task");

        Assert.Equal(new[] { "To Do", "Done" }, states);
    }

    [Fact]
    public async Task AdminUpdate_DisablingAState_RemovesItFromPublicGet()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();
        var created = await admin.PostAsJsonAsync(
            "/api/admin/work-item-states",
            new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "Fixed", DisplayOrder = 0, IsEnabled = true });
        var view = await created.Content.ReadFromJsonAsync<WorkItemStateView>();

        var updateResponse = await admin.PutAsJsonAsync(
            $"/api/admin/work-item-states/{view!.Id}",
            new UpdateWorkItemStateRequest { StateName = "Fixed", DisplayOrder = 0, IsEnabled = false });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var states = await admin.GetFromJsonAsync<List<string>>("/api/work-item-states/Bug");
        Assert.DoesNotContain("Fixed", states!);
    }

    [Fact]
    public async Task AdminUpdate_NonExistentId_ReturnsNotFound()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();

        var response = await admin.PutAsJsonAsync(
            "/api/admin/work-item-states/999999",
            new UpdateWorkItemStateRequest { StateName = "Whatever", DisplayOrder = 0, IsEnabled = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminDelete_RemovesTheState()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();
        var created = await admin.PostAsJsonAsync(
            "/api/admin/work-item-states",
            new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "Wontfix", DisplayOrder = 0, IsEnabled = true });
        var view = await created.Content.ReadFromJsonAsync<WorkItemStateView>();

        var deleteResponse = await admin.DeleteAsync($"/api/admin/work-item-states/{view!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var states = await admin.GetFromJsonAsync<List<string>>("/api/work-item-states/Bug");
        Assert.DoesNotContain("Wontfix", states!);
    }

    [Fact]
    public async Task AdminDelete_NonExistentId_ReturnsNotFound()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();

        var response = await admin.DeleteAsync("/api/admin/work-item-states/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminGet_GroupsMultipleTypesSeparately()
    {
        using var admin = await CreateAuthenticatedAdminClientAsync();
        await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "New", DisplayOrder = 0, IsEnabled = true });
        await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Feature", StateName = "Planned", DisplayOrder = 0, IsEnabled = true });

        var groups = await admin.GetFromJsonAsync<List<WorkItemTypeStatesView>>("/api/admin/work-item-states");

        Assert.Contains(groups!, g => g.WorkItemType == "Bug");
        Assert.Contains(groups!, g => g.WorkItemType == "Feature");
    }
}
