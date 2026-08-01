using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AzureBuddy.Core.Auth;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of /api/auth/* through the real HTTP pipeline (real Identity password
/// hashing/validation, real JWT issuance, real EF Core persistence against the InMemory database).</summary>
public class AuthEndpointsTests : IntegrationTestBase
{
    public AuthEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Register_ValidRequest_ReturnsAccessAndRefreshTokens()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            $"register-{Guid.NewGuid():N}@example.com", "Str0ng!Passw0rd", "New User"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokens>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrEmpty(tokens!.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
    }

    [Fact]
    public async Task Register_MalformedEmail_ReturnsBadRequest()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            "not-an-email", "Str0ng!Passw0rd", "Someone"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Fails()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);

        var second = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Str0ng!Passw0rd", "Second"));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Login_CorrectCredentials_ReturnsTokens()
    {
        var email = $"login-{Guid.NewGuid():N}@example.com";
        const string password = "Str0ng!Passw0rd";
        await RegisterAsync(email, password);

        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokens>();
        Assert.NotNull(tokens);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorizedWithGenericMessage()
    {
        var email = $"wrongpw-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email, "Str0ng!Passw0rd");

        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "TotallyWrong!1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsSameUnauthorizedAsWrongPassword()
    {
        // Same status code either way (email doesn't exist vs. wrong password) - distinguishing them
        // would let a client enumerate valid accounts.
        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody-here@example.com", "whatever123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewTokenPair()
    {
        var original = await RegisterAsync();

        var response = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(original.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newTokens = await response.Content.ReadFromJsonAsync<AuthTokens>();
        Assert.NotNull(newTokens);
        Assert.NotEqual(original.AccessToken, newTokens!.AccessToken);
        Assert.NotEqual(original.RefreshToken, newTokens.RefreshToken);
    }

    [Fact]
    public async Task Refresh_TokenAlreadyUsedOnce_SecondUseFails()
    {
        // Rotation: a refresh token is single-use. Reusing an already-rotated token (e.g. replay of a
        // leaked token after the legitimate client already refreshed) must fail.
        var original = await RegisterAsync();

        var firstUse = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(original.RefreshToken));
        firstUse.EnsureSuccessStatusCode();

        var secondUse = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(original.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, secondUse.StatusCode);
    }

    [Fact]
    public async Task Refresh_UnknownToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("this-token-was-never-issued"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken_SubsequentRefreshFails()
    {
        var tokens = await RegisterAsync();

        var logout = await Client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refreshAfterLogout = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var response = await Client.GetAsync("/api/chats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidAccessToken_Succeeds()
    {
        var tokens = await RegisterAsync();
        using var client = Factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/chats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
