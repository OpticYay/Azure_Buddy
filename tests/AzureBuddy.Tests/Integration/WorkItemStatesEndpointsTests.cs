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
        // "Verified" (not "Active" - already seeded for "Bug", see AdminCreate_ThenPublicGet's comment
        // above) so the create below actually has to succeed for this assertion to pass, rather than
        // being trivially satisfied by pre-existing seed data regardless of whether it did.
        using var admin = await CreateAuthenticatedAdminClientAsync();
        var createResponse = await admin.PostAsJsonAsync(
            "/api/admin/work-item-states",
            new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "Verified", DisplayOrder = 0, IsEnabled = true });
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        using var regularUser = await CreateAuthenticatedClientAsync();
        var response = await regularUser.GetAsync("/api/work-item-states/Bug");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var states = await response.Content.ReadFromJsonAsync<List<string>>();
        Assert.Contains("Verified", states!);
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
        // "Task"/"To Do" (DisplayOrder 0), "In Progress" (1), and "Done" (2) are already seeded by the
        // AddWorkItemStateConfigurations migration, which now actually runs against the real MySQL
        // database this test suite uses - previously, under EF Core's InMemory provider, migrations
        // never ran at all, so this test never collided with seed data. "Blocked"/"Review" aren't part
        // of that seeded set, and their DisplayOrder values (10/11) are chosen clear of the seeded
        // 0-2 range so the expected order doesn't depend on how ties within the same DisplayOrder are
        // broken.
        using var admin = await CreateAuthenticatedAdminClientAsync();
        await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Task", StateName = "Review", DisplayOrder = 11, IsEnabled = true });
        await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Task", StateName = "Blocked", DisplayOrder = 10, IsEnabled = true });

        var states = await admin.GetFromJsonAsync<List<string>>("/api/work-item-states/Task");

        Assert.Equal(new[] { "To Do", "In Progress", "Done", "Blocked", "Review" }, states);
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
        // "Triaged" (not "New" - already seeded for "Bug") so this create actually has to succeed;
        // the "Bug" assertion below would otherwise pass regardless, since "Bug" already has seeded
        // states with or without this POST succeeding.
        using var admin = await CreateAuthenticatedAdminClientAsync();
        var bugResponse = await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Bug", StateName = "Triaged", DisplayOrder = 0, IsEnabled = true });
        Assert.Equal(HttpStatusCode.OK, bugResponse.StatusCode);
        var featureResponse = await admin.PostAsJsonAsync("/api/admin/work-item-states", new CreateWorkItemStateRequest { WorkItemType = "Feature", StateName = "Planned", DisplayOrder = 0, IsEnabled = true });
        Assert.Equal(HttpStatusCode.OK, featureResponse.StatusCode);

        var groups = await admin.GetFromJsonAsync<List<WorkItemTypeStatesView>>("/api/admin/work-item-states");

        Assert.Contains(groups!, g => g.WorkItemType == "Bug");
        Assert.Contains(groups!, g => g.WorkItemType == "Feature");
    }
}
