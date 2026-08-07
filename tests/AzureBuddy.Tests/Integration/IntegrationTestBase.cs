using System.Net.Http.Headers;
using System.Net.Http.Json;
using AzureBuddy.Core.Auth;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>Shared setup/helpers for endpoint integration tests - registering a user and getting an
/// authenticated HttpClient is boilerplate every test class needs, so it lives here once. The
/// instance-level methods (RegisterAsync, CreateAuthenticatedClientAsync, ...) operate against the
/// class-shared Factory/DB; the static *OnAsync variants take an explicit factory for the rare test
/// that needs its own fully isolated instance (see LlmSettingsEndpointsTests for why that matters for
/// a true singleton row).</summary>
public abstract class IntegrationTestBase : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    protected readonly CustomWebApplicationFactory Factory;
    protected readonly HttpClient Client;

    protected IntegrationTestBase(CustomWebApplicationFactory factory)
    {
        Factory = factory;
        Factory.AdoClient.Reset();
        Factory.EmailSender.Reset();
        Client = factory.CreateHttpsClient();
    }

    protected Task<AuthTokens> RegisterAsync(string? email = null, string? password = null, string? displayName = null) =>
        RegisterOnAsync(Client, email, password, displayName);

    /// <summary>Registers a brand-new user and returns an HttpClient with its access token already
    /// attached as a Bearer token, ready to call any [Authorize]d endpoint.</summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync(string? email = null)
    {
        var tokens = await RegisterAsync(email);
        var client = Factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>
    /// Registers a brand-new user, promotes it to the "Admin" role directly through Identity's
    /// RoleManager/UserManager (bypassing the Admin:Emails startup-bootstrap mechanism entirely - see
    /// Program.cs's "Admin role seeding" block, which only runs once at host startup and can't see a
    /// user created after that), then logs in again so the returned access token's "role" claim
    /// actually reflects the promotion (TokenService.CreateAccessToken reads roles fresh at login
    /// time - the token from registration, issued before the promotion, would not have it).
    /// </summary>
    protected Task<HttpClient> CreateAuthenticatedAdminClientAsync(string? email = null) =>
        CreateAuthenticatedAdminClientOnAsync(Factory, email);

    private static async Task<AuthTokens> RegisterOnAsync(HttpClient client, string? email = null, string? password = null, string? displayName = null)
    {
        var request = new RegisterRequest(
            email ?? $"user-{Guid.NewGuid():N}@example.com",
            password ?? "Str0ng!Passw0rd",
            displayName ?? "Test User");

        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AuthTokens>())!;
    }

    /// <summary>Same as CreateAuthenticatedAdminClientAsync, but against an explicit factory instead
    /// of the class-shared one - use this from a test that constructs its own
    /// `new CustomWebApplicationFactory()` to get a fully isolated database.</summary>
    protected static async Task<HttpClient> CreateAuthenticatedAdminClientOnAsync(CustomWebApplicationFactory factory, string? email = null)
    {
        email ??= $"admin-{Guid.NewGuid():N}@example.com";
        const string password = "Str0ng!Passw0rd";

        using (var registerClient = factory.CreateHttpsClient())
        {
            await RegisterOnAsync(registerClient, email, password);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleManager.RoleExistsAsync("Admin"))
            {
                await roleManager.CreateAsync(new IdentityRole("Admin"));
            }

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            await userManager.AddToRoleAsync(user!, "Admin");
        }

        using var loginClient = factory.CreateHttpsClient();
        var loginResponse = await loginClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        loginResponse.EnsureSuccessStatusCode();
        var tokens = (await loginResponse.Content.ReadFromJsonAsync<AuthTokens>())!;

        var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    public void Dispose()
    {
        Client.Dispose();
    }
}
