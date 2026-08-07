using System.Net;
using System.Net.Http.Json;
using AzureBuddy.Core.Llm;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of /api/admin/llm - the Admin-only gate, and that saving takes effect
/// on the same GET without needing a restart (LlmSettingsService.SaveAsync's whole point).</summary>
public class LlmSettingsEndpointsTests : IntegrationTestBase
{
    public LlmSettingsEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    private static SaveLlmSettingsRequest ValidRequest(string? geminiApiKey = "a-fresh-gemini-key") => new(
        PrimaryProvider: "Gemini",
        UseFallback: true,
        GeminiModel: "gemini-1.5-flash",
        GeminiBaseUrl: "https://generativelanguage.googleapis.com/v1beta",
        GeminiTimeoutSeconds: 30,
        GeminiApiKey: geminiApiKey,
        OllamaModel: "qwen2.5:14b-instruct",
        OllamaBaseUrl: "http://localhost:11434",
        OllamaNumCtx: 8192,
        OllamaTimeoutSeconds: 60);

    [Fact]
    public async Task Get_AsNonAdmin_ReturnsForbidden()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/llm");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_AsNonAdmin_ReturnsForbidden()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/admin/llm", ValidRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_Unauthenticated_ReturnsUnauthorized()
    {
        var response = await Client.GetAsync("/api/admin/llm");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsAdmin_NoRowSavedYet_ReflectsAppConfigDefaults()
    {
        // LlmSettings is a true singleton row (LlmSettings.SingletonId), unlike every other entity in
        // this suite - a PUT from any other test in this class would leave a saved row behind and
        // make this assertion order-dependent against the shared class-level Factory/DB. A dedicated,
        // fresh factory+DB sidesteps that instead of relying on test execution order within the class.
        // xUnit only calls IAsyncLifetime.InitializeAsync() automatically for factories it manages via
        // IClassFixture<T> - a manually-constructed instance like this one needs an explicit call, or
        // its database is never created/migrated (ConfigureWebHost would register AppDbContext with an
        // empty connection string instead).
        await using var isolatedFactory = new CustomWebApplicationFactory();
        await isolatedFactory.InitializeAsync();
        using var client = await CreateAuthenticatedAdminClientOnAsync(isolatedFactory);

        var view = await client.GetFromJsonAsync<LlmSettingsView>("/api/admin/llm");

        Assert.NotNull(view);
        Assert.False(view!.IsStoredInDatabase);
    }

    [Fact]
    public async Task Put_AsAdmin_ValidRequest_PersistsAndMasksKey()
    {
        using var client = await CreateAuthenticatedAdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/admin/llm", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<LlmSettingsView>();

        Assert.True(view!.IsStoredInDatabase);
        Assert.Equal("Gemini", view.PrimaryProvider);
        Assert.True(view.UseFallback);
        Assert.NotNull(view.MaskedGeminiApiKey);
        Assert.DoesNotContain("a-fresh-gemini-key", view.MaskedGeminiApiKey);
        Assert.EndsWith("-key", view.MaskedGeminiApiKey);
    }

    [Fact]
    public async Task Put_TakesEffectImmediately_ReflectedOnSubsequentGet()
    {
        using var client = await CreateAuthenticatedAdminClientAsync();
        await client.PutAsJsonAsync("/api/admin/llm", ValidRequest() with { OllamaTimeoutSeconds = 123 });

        var view = await client.GetFromJsonAsync<LlmSettingsView>("/api/admin/llm");

        Assert.Equal(123, view!.OllamaTimeoutSeconds);
    }

    [Fact]
    public async Task Put_BlankApiKeyOnSecondSave_LeavesPreviouslySavedKeyIntact()
    {
        using var client = await CreateAuthenticatedAdminClientAsync();
        await client.PutAsJsonAsync("/api/admin/llm", ValidRequest("first-real-key"));

        var second = await client.PutAsJsonAsync("/api/admin/llm", ValidRequest(geminiApiKey: null) with { OllamaModel = "a-different-model" });
        var view = await second.Content.ReadFromJsonAsync<LlmSettingsView>();

        Assert.NotNull(view!.MaskedGeminiApiKey);
        Assert.EndsWith("-key", view.MaskedGeminiApiKey); // still masks "first-real-key", not cleared
        Assert.Equal("a-different-model", view.OllamaModel);
    }

    [Fact]
    public async Task Put_UnknownProvider_ReturnsBadRequest()
    {
        using var client = await CreateAuthenticatedAdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/admin/llm", ValidRequest() with { PrimaryProvider = "NotAKnownProvider" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_MissingRequiredField_ReturnsBadRequest()
    {
        using var client = await CreateAuthenticatedAdminClientAsync();

        var response = await client.PutAsJsonAsync("/api/admin/llm", new { primaryProvider = "Gemini" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
