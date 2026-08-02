using System.IdentityModel.Tokens.Jwt;
using AzureBuddy.Core.Auth;
using AzureBuddy.Data.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureBuddy.Tests.Auth;

public class TokenServiceTests
{
    private static TokenService CreateService(int accessTokenMinutes = 15) => new(Options.Create(new JwtOptions
    {
        SigningKey = "unit-test-signing-key-at-least-32-bytes-long!!",
        Issuer = "AzureBuddyTests",
        Audience = "AzureBuddyTests",
        AccessTokenMinutes = accessTokenMinutes,
        RefreshTokenDays = 30
    }));

    private static ApplicationUser CreateUser() => new()
    {
        Id = "user-123",
        Email = "qa@example.com",
        UserName = "qa@example.com"
    };

    [Fact]
    public void CreateAccessToken_IncludesUserIdAndEmailClaims()
    {
        var service = CreateService();
        var user = CreateUser();

        var result = service.CreateAccessToken(user, Array.Empty<string>());
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        Assert.Equal("user-123", token.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("qa@example.com", token.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value);
    }

    [Fact]
    public void CreateAccessToken_SetsIssuerAndAudienceFromOptions()
    {
        var service = CreateService();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateAccessToken(CreateUser(), Array.Empty<string>()).Token);

        Assert.Equal("AzureBuddyTests", token.Issuer);
        Assert.Contains("AzureBuddyTests", token.Audiences);
    }

    [Fact]
    public void CreateAccessToken_ExpiresAccordingToConfiguredMinutes()
    {
        var service = CreateService(accessTokenMinutes: 15);
        var before = DateTime.UtcNow;

        var result = service.CreateAccessToken(CreateUser(), Array.Empty<string>());

        var expectedExpiry = before.AddMinutes(15);
        Assert.True(Math.Abs((result.ExpiresAtUtc - expectedExpiry).TotalSeconds) < 5,
            $"Expected expiry near {expectedExpiry:o}, got {result.ExpiresAtUtc:o}");
    }

    [Fact]
    public void CreateAccessToken_EachTokenHasAUniqueJtiClaim()
    {
        var service = CreateService();
        var user = CreateUser();

        var token1 = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateAccessToken(user, Array.Empty<string>()).Token);
        var token2 = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateAccessToken(user, Array.Empty<string>()).Token);

        var jti1 = token1.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var jti2 = token2.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        Assert.NotEqual(jti1, jti2);
    }

    [Fact]
    public void CreateAccessToken_IncludesOneRoleClaimPerRole()
    {
        var service = CreateService();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            service.CreateAccessToken(CreateUser(), new[] { "Admin", "Beta" }).Token);

        var roleClaims = token.Claims.Where(c => c.Type == "role").Select(c => c.Value).ToList();
        Assert.Equal(new[] { "Admin", "Beta" }, roleClaims);
    }

    [Fact]
    public void CreateAccessToken_NoRoles_HasNoRoleClaims()
    {
        var service = CreateService();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            service.CreateAccessToken(CreateUser(), Array.Empty<string>()).Token);

        Assert.DoesNotContain(token.Claims, c => c.Type == "role");
    }

    [Fact]
    public void GenerateRefreshToken_ProducesDifferentValuesEachCall()
    {
        var service = CreateService();

        var token1 = service.GenerateRefreshToken();
        var token2 = service.GenerateRefreshToken();

        Assert.NotEqual(token1, token2);
        Assert.True(token1.Length > 20);
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        Assert.Equal(TokenService.HashToken("same-input"), TokenService.HashToken("same-input"));
    }

    [Fact]
    public void HashToken_DifferentInputsProduceDifferentHashes()
    {
        Assert.NotEqual(TokenService.HashToken("input-a"), TokenService.HashToken("input-b"));
    }

    [Fact]
    public void HashToken_NeverEqualsItsOwnInput()
    {
        const string token = "a-refresh-token-value";
        Assert.NotEqual(token, TokenService.HashToken(token));
    }
}
