using System.Net;
using System.Net.Http.Json;
using AzureBuddy.Core.Settings;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of /api/settings/ado - PAT masking, validation, and the user-scoping
/// that prevents one user from reading/overwriting another user's ADO settings (IDOR).</summary>
public class SettingsEndpointsTests : IntegrationTestBase
{
    public SettingsEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Get_NoSettingsSavedYet_ReturnsNotConfigured()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var view = await client.GetFromJsonAsync<AdoSettingsView>("/api/settings/ado");

        Assert.NotNull(view);
        Assert.False(view!.IsConfigured);
        Assert.Null(view.OrganizationUrl);
        Assert.Null(view.MaskedPat);
    }

    [Fact]
    public async Task Put_ValidRequest_PersistsAndReturnsMaskedPat()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest(
            "https://dev.azure.com/myorg", "MyProject", "super-secret-pat-value-1234"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<AdoSettingsView>();

        Assert.True(view!.IsConfigured);
        Assert.Equal("https://dev.azure.com/myorg", view.OrganizationUrl);
        Assert.Equal("MyProject", view.DefaultProject);
        Assert.NotNull(view.MaskedPat);
        Assert.DoesNotContain("super-secret-pat-value-1234", view.MaskedPat);
        Assert.EndsWith("1234", view.MaskedPat); // last 4 chars visible, matching the masking rule
    }

    [Fact]
    public async Task Put_MissingOrganizationUrl_ReturnsBadRequest()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings/ado", new { defaultProject = "Proj", personalAccessToken = "pat" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_MalformedOrganizationUrl_ReturnsBadRequest()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest(
            "not-a-url", "MyProject", "pat-value"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_CalledTwice_UpsertsRatherThanDuplicating()
    {
        using var client = await CreateAuthenticatedClientAsync();

        await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org1", "Proj1", "pat1"));
        var second = await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org2", "Proj2", "pat2222"));
        var view = await second.Content.ReadFromJsonAsync<AdoSettingsView>();

        Assert.Equal("https://dev.azure.com/org2", view!.OrganizationUrl);
        Assert.Equal("Proj2", view.DefaultProject);

        var getResponse = await client.GetFromJsonAsync<AdoSettingsView>("/api/settings/ado");
        Assert.Equal("https://dev.azure.com/org2", getResponse!.OrganizationUrl);
    }

    [Fact]
    public async Task Delete_RemovesSettings()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org", "Proj", "pat-value"));

        var deleteResponse = await client.DeleteAsync("/api/settings/ado");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var view = await client.GetFromJsonAsync<AdoSettingsView>("/api/settings/ado");
        Assert.False(view!.IsConfigured);
    }

    [Fact]
    public async Task TestConnection_NoSettingsSaved_ReturnsFailureWithoutCallingAdo()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/settings/ado/test-connection", null);
        var result = await response.Content.ReadFromJsonAsync<TestConnectionResult>();

        Assert.False(result!.Success);
        Assert.Empty(Factory.AdoClient.Calls);
    }

    [Fact]
    public async Task TestConnection_AdoAcceptsThePat_ReturnsSuccess()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org", "Proj", "pat-value"));
        Factory.AdoClient.TestConnectionBehavior = _ => true;

        var response = await client.PostAsync("/api/settings/ado/test-connection", null);
        var result = await response.Content.ReadFromJsonAsync<TestConnectionResult>();

        Assert.True(result!.Success);
    }

    [Fact]
    public async Task TestConnection_AdoRejectsThePat_ReturnsFailureWithMessage()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/org", "Proj", "bad-pat"));
        Factory.AdoClient.TestConnectionBehavior = _ => false;

        var response = await client.PostAsync("/api/settings/ado/test-connection", null);
        var result = await response.Content.ReadFromJsonAsync<TestConnectionResult>();

        Assert.False(result!.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task UserA_CannotSeeUserB_AdoSettings()
    {
        using var clientA = await CreateAuthenticatedClientAsync();
        await clientA.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/orgA", "ProjA", "pat-for-a"));

        using var clientB = await CreateAuthenticatedClientAsync();
        var viewFromB = await clientB.GetFromJsonAsync<AdoSettingsView>("/api/settings/ado");

        Assert.False(viewFromB!.IsConfigured);
        Assert.Null(viewFromB.OrganizationUrl);
    }

    [Fact]
    public async Task UserA_CannotOverwriteUserB_AdoSettingsByCallingPutOnTheirOwnClient()
    {
        // There's no sessionId/settingsId in the route at all - PUT always targets "my own" row, so
        // this test documents that guarantee rather than probing for a route-level IDOR (there isn't
        // one to probe, by construction).
        using var clientA = await CreateAuthenticatedClientAsync();
        await clientA.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/orgA", "ProjA", "pat-a"));

        using var clientB = await CreateAuthenticatedClientAsync();
        await clientB.PutAsJsonAsync("/api/settings/ado", new SaveAdoSettingsRequest("https://dev.azure.com/orgB", "ProjB", "pat-b"));

        var viewA = await clientA.GetFromJsonAsync<AdoSettingsView>("/api/settings/ado");
        var viewB = await clientB.GetFromJsonAsync<AdoSettingsView>("/api/settings/ado");

        Assert.Equal("https://dev.azure.com/orgA", viewA!.OrganizationUrl);
        Assert.Equal("https://dev.azure.com/orgB", viewB!.OrganizationUrl);
    }
}
