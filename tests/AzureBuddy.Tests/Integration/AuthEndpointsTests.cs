using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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

    [Fact]
    public async Task Register_SendsConfirmationEmail()
    {
        var email = $"confirm-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);

        var sent = Assert.Single(Factory.EmailSender.SentEmails);
        Assert.Equal(email, sent.ToEmail);
        Assert.Contains("confirm-email", sent.Body);
    }

    [Fact]
    public async Task ForgotPassword_KnownEmail_SendsResetLinkAndAlwaysReturnsNoContent()
    {
        var email = $"forgot-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);
        Factory.EmailSender.Reset(); // clear the registration-confirmation email so only the reset one remains

        var response = await Client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var sent = Assert.Single(Factory.EmailSender.SentEmails);
        Assert.Equal(email, sent.ToEmail);
        Assert.Contains("reset-password", sent.Body);
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_StillReturnsNoContentAndSendsNothing()
    {
        // Non-enumeration: identical response whether or not the account exists.
        var response = await Client.PostAsJsonAsync(
            "/api/auth/forgot-password", new ForgotPasswordRequest($"nobody-{Guid.NewGuid():N}@example.com"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Factory.EmailSender.SentEmails);
    }

    [Fact]
    public async Task ResetPassword_ValidToken_ChangesPasswordAndReturnsTokens()
    {
        var email = $"reset-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email, "OldPassw0rd!");
        await Client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));
        var (linkEmail, token) = ExtractEmailAndTokenFromLink(Factory.EmailSender.SentEmails.Last().Body);

        var resetResponse = await Client.PostAsJsonAsync(
            "/api/auth/reset-password", new ResetPasswordRequest(linkEmail, token, "BrandNewPassw0rd!"));

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        var tokens = await resetResponse.Content.ReadFromJsonAsync<AuthTokens>();
        Assert.NotNull(tokens);

        // The old password no longer works; the new one does.
        var loginWithOld = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "OldPassw0rd!"));
        Assert.Equal(HttpStatusCode.Unauthorized, loginWithOld.StatusCode);

        var loginWithNew = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "BrandNewPassw0rd!"));
        Assert.Equal(HttpStatusCode.OK, loginWithNew.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_TokenAlreadyUsedOnce_SecondUseFails()
    {
        var email = $"reset-reuse-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email, "OldPassw0rd!");
        await Client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));
        var (linkEmail, token) = ExtractEmailAndTokenFromLink(Factory.EmailSender.SentEmails.Last().Body);

        var first = await Client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(linkEmail, token, "FirstNewPassw0rd!"));
        first.EnsureSuccessStatusCode();

        var second = await Client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(linkEmail, token, "SecondNewPassw0rd!"));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_GarbageToken_ReturnsBadRequestNotServerError()
    {
        var email = $"reset-garbage-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);

        var response = await Client.PostAsJsonAsync(
            "/api/auth/reset-password", new ResetPasswordRequest(email, "not-a-real-token", "SomeNewPassw0rd!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_UnknownEmail_ReturnsBadRequestSameAsInvalidToken()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest($"nobody-{Guid.NewGuid():N}@example.com", "irrelevant-token", "SomeNewPassw0rd!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_ValidToken_ConfirmsAndReturnsTokens()
    {
        var email = $"confirmflow-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);
        var (linkEmail, token) = ExtractEmailAndTokenFromLink(Factory.EmailSender.SentEmails.Single().Body);

        var response = await Client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(linkEmail, token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokens>();
        Assert.NotNull(tokens);
    }

    [Fact]
    public async Task ConfirmEmail_GarbageToken_ReturnsBadRequest()
    {
        var email = $"confirmbad-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);

        var response = await Client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(email, "not-a-real-token"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResendConfirmation_UnknownEmail_StillReturnsNoContentAndSendsNothing()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/resend-confirmation", new ResendConfirmationRequest($"nobody-{Guid.NewGuid():N}@example.com"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Factory.EmailSender.SentEmails);
    }

    [Fact]
    public async Task ResendConfirmation_AlreadyConfirmed_SendsNothing()
    {
        var email = $"resend-confirmed-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);
        var (linkEmail, token) = ExtractEmailAndTokenFromLink(Factory.EmailSender.SentEmails.Single().Body);
        await Client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(linkEmail, token));
        Factory.EmailSender.Reset();

        var response = await Client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmationRequest(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(Factory.EmailSender.SentEmails);
    }

    [Fact]
    public async Task ResendConfirmation_UnconfirmedEmail_SendsANewConfirmationLink()
    {
        var email = $"resend-unconfirmed-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(email);
        Factory.EmailSender.Reset();

        var response = await Client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmationRequest(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var sent = Assert.Single(Factory.EmailSender.SentEmails);
        Assert.Contains("confirm-email", sent.Body);
    }

    /// <summary>Pulls email/token straight out of the link AuthService.BuildLink embeds in the email
    /// body (e.g. ".../reset-password?email=x%40y.com&amp;token=abc123") - mirrors what the frontend's
    /// reset-password/confirm-email pages do by reading their own route's query params.</summary>
    private static (string Email, string Token) ExtractEmailAndTokenFromLink(string emailBody)
    {
        var match = Regex.Match(emailBody, @"[?&]email=(?<email>[^&\s]+)&token=(?<token>[^&\s]+)");
        Assert.True(match.Success, $"Could not find an email/token link in body: {emailBody}");
        return (Uri.UnescapeDataString(match.Groups["email"].Value), Uri.UnescapeDataString(match.Groups["token"].Value));
    }
}
