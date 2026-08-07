using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AzureBuddy.Api.Controllers;
using AzureBuddy.Core.Account;
using AzureBuddy.Core.Auth;
using Xunit;

namespace AzureBuddy.Tests.Integration;

/// <summary>End-to-end tests of /api/account/* - profile view/update (including the duplicate-email
/// conflict and the requiresReLogin flag), and change-password (including Identity's own current-
/// password verification and the other-sessions-revoked-but-not-this-one behavior).</summary>
public class AccountEndpointsTests : IntegrationTestBase
{
    public AccountEndpointsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    private HttpClient ClientFor(AuthTokens tokens)
    {
        var client = Factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task GetProfile_Unauthenticated_ReturnsUnauthorized()
    {
        var response = await Client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetProfile_ReturnsEmailAndDisplayName()
    {
        var email = $"profile-{Guid.NewGuid():N}@example.com";
        var tokens = await RegisterAsync(email, displayName: "Original Name");
        using var client = ClientFor(tokens);

        var profile = await client.GetFromJsonAsync<ProfileView>("/api/account/profile");

        Assert.NotNull(profile);
        Assert.Equal(email, profile!.Email);
        Assert.Equal("Original Name", profile.DisplayName);
        Assert.Empty(profile.Roles);
    }

    [Fact]
    public async Task UpdateProfile_DisplayNameOnly_UpdatesAndDoesNotRequireReLogin()
    {
        var email = $"displayname-{Guid.NewGuid():N}@example.com";
        var tokens = await RegisterAsync(email, displayName: "Old Name");
        using var client = ClientFor(tokens);

        var response = await client.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("New Name", email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UpdateProfileResponse>();
        Assert.NotNull(body);
        Assert.Equal("New Name", body!.Profile.DisplayName);
        Assert.False(body.RequiresReLogin);
    }

    [Fact]
    public async Task UpdateProfile_EmailChange_RequiresReLoginTrue()
    {
        var oldEmail = $"oldemail-{Guid.NewGuid():N}@example.com";
        var newEmail = $"newemail-{Guid.NewGuid():N}@example.com";
        var tokens = await RegisterAsync(oldEmail, displayName: "Someone");
        using var client = ClientFor(tokens);

        var response = await client.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("Someone", newEmail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UpdateProfileResponse>();
        Assert.Equal(newEmail, body!.Profile.Email);
        Assert.True(body.RequiresReLogin);

        // The new email must actually work for a fresh login - proves SetEmailAsync/SetUserNameAsync
        // updated the Normalized* columns Identity actually looks up by, not just the raw Email column.
        var loginResponse = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(newEmail, "Str0ng!Passw0rd"));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_EmailAlreadyUsedByAnotherAccount_ReturnsConflict()
    {
        var takenEmail = $"taken-{Guid.NewGuid():N}@example.com";
        await RegisterAsync(takenEmail);

        var tokens = await RegisterAsync($"other-{Guid.NewGuid():N}@example.com", displayName: "Other User");
        using var client = ClientFor(tokens);

        var response = await client.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("Other User", takenEmail));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_KeepingOwnCurrentEmail_IsNotTreatedAsAConflictWithSelf()
    {
        var email = $"self-{Guid.NewGuid():N}@example.com";
        var tokens = await RegisterAsync(email, displayName: "Old Name");
        using var client = ClientFor(tokens);

        // Same email as already owned, only display name changes - must not be rejected as "taken by
        // another account" just because FindByEmailAsync finds the caller's own row.
        var response = await client.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("New Name", email));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_SucceedsAndNewPasswordLogsIn()
    {
        var email = $"changepw-{Guid.NewGuid():N}@example.com";
        var tokens = await RegisterAsync(email, "OldPassw0rd!");
        using var client = ClientFor(tokens);

        var response = await client.PostAsJsonAsync(
            "/api/account/change-password", new ChangePasswordRequest("OldPassw0rd!", "BrandNewPassw0rd!", tokens.RefreshToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var oldLogin = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "OldPassw0rd!"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "BrandNewPassw0rd!"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsBadRequestOnCurrentPasswordField()
    {
        var tokens = await RegisterAsync(password: "ActualPassw0rd!");
        using var client = ClientFor(tokens);

        var response = await client.PostAsJsonAsync(
            "/api/account/change-password", new ChangePasswordRequest("TotallyWrong!1", "SomeNewPassw0rd!", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.Contains(errors!.Errors, e => e.Field == "currentPassword");
    }

    [Fact]
    public async Task ChangePassword_NewPasswordFailsPolicy_ReturnsBadRequestOnNewPasswordField()
    {
        var tokens = await RegisterAsync(password: "ActualPassw0rd!");
        using var client = ClientFor(tokens);

        // Too short and missing required character classes per the configured Identity password policy.
        var response = await client.PostAsJsonAsync(
            "/api/account/change-password", new ChangePasswordRequest("ActualPassw0rd!", "weak", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = await response.Content.ReadFromJsonAsync<ApiErrorResponseDto>();
        Assert.Contains(errors!.Errors, e => e.Field == "newPassword");
    }

    [Fact]
    public async Task ChangePassword_WithCurrentRefreshTokenSupplied_RevokesOtherSessionsButKeepsThisOneAlive()
    {
        var email = $"multisession-{Guid.NewGuid():N}@example.com";
        const string password = "OldPassw0rd!";
        var sessionA = await RegisterAsync(email, password); // this "device"
        var loginB = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        var sessionB = (await loginB.Content.ReadFromJsonAsync<AuthTokens>())!; // a second "device"

        using var clientA = ClientFor(sessionA);
        var changeResponse = await clientA.PostAsJsonAsync(
            "/api/account/change-password", new ChangePasswordRequest(password, "BrandNewPassw0rd!", sessionA.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, changeResponse.StatusCode);

        // Session A's own refresh token was excluded from revocation - it must still work.
        var refreshA = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(sessionA.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshA.StatusCode);

        // Session B never identified itself as "the current one" to change-password, so it must be revoked.
        var refreshB = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(sessionB.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshB.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_NoRefreshTokenSupplied_RevokesEverySession()
    {
        var email = $"norefresh-{Guid.NewGuid():N}@example.com";
        const string password = "OldPassw0rd!";
        var tokens = await RegisterAsync(email, password);
        using var client = ClientFor(tokens);

        var response = await client.PostAsJsonAsync(
            "/api/account/change-password", new ChangePasswordRequest(password, "BrandNewPassw0rd!", null));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var refresh = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    /// <summary>Local mirror of ApiErrorResponse just for deserializing in these tests - the real type
    /// lives in AzureBuddy.Core.Common but its record ctor shape deserializes fine as a plain DTO.</summary>
    private sealed record ApiErrorResponseDto(List<ApiErrorDto> Errors);
    private sealed record ApiErrorDto(string Code, string Message, string? Field);
}
