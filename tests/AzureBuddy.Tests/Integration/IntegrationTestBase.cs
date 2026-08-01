using System.Net.Http.Headers;
using System.Net.Http.Json;
using AzureBuddy.Core.Auth;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>Shared setup/helpers for endpoint integration tests - registering a user and getting an
/// authenticated HttpClient is boilerplate every test class needs, so it lives here once.</summary>
public abstract class IntegrationTestBase : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    protected readonly CustomWebApplicationFactory Factory;
    protected readonly HttpClient Client;

    protected IntegrationTestBase(CustomWebApplicationFactory factory)
    {
        Factory = factory;
        Factory.AdoClient.Reset();
        Client = factory.CreateHttpsClient();
    }

    protected async Task<AuthTokens> RegisterAsync(string? email = null, string? password = null, string? displayName = null)
    {
        var request = new RegisterRequest(
            email ?? $"user-{Guid.NewGuid():N}@example.com",
            password ?? "Str0ng!Passw0rd",
            displayName ?? "Test User");

        var response = await Client.PostAsJsonAsync("/api/auth/register", request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    /// <summary>Registers a brand-new user and returns an HttpClient with its access token already
    /// attached as a Bearer token, ready to call any [Authorize]d endpoint.</summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync(string? email = null)
    {
        var tokens = await RegisterAsync(email);
        var client = Factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public void Dispose()
    {
        Client.Dispose();
    }
}
