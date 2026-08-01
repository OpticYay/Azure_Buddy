using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AzureBuddy.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzureBuddy.Core.Auth;

public sealed record AccessTokenResult(string Token, DateTime ExpiresAtUtc);

/// <summary>
/// Everything token-shaped lives here: minting short-lived signed access tokens, and generating/
/// hashing the long-lived refresh tokens. Kept separate from AuthController so the actual crypto isn't
/// mixed in with HTTP/request-handling concerns, and so it's unit-testable without spinning up ASP.NET.
/// </summary>
public sealed class TokenService
{
    private readonly JwtOptions _options;

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Builds a signed JWT containing the user's id (as the "sub" claim) and email. Every authenticated
    /// endpoint reads the user's identity back out of this token's claims via HttpContext.User - never
    /// from a client-supplied userId in the request body/query string, which is what makes IDOR
    /// (Insecure Direct Object Reference - one user fetching another user's data by guessing an id)
    /// impossible for anything keyed off "the current user".
    /// </summary>
    public AccessTokenResult CreateAccessToken(ApplicationUser user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    /// <summary>Generates a cryptographically random opaque string (not a JWT - it doesn't need to be
    /// self-describing, it's just a long-lived credential we look up by hash in the RefreshTokens table).</summary>
    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    public DateTime RefreshTokenExpiry() => DateTime.UtcNow.AddDays(_options.RefreshTokenDays);

    /// <summary>
    /// We never store a refresh token in the database as plaintext - only its hash (same principle as
    /// password hashing). SHA-256 (not a slow hash like PBKDF2/bcrypt) is appropriate here because,
    /// unlike a password, a refresh token is already a 512-bit random value - there's no low-entropy
    /// input for an attacker to brute-force, so a slow hash buys nothing extra.
    /// </summary>
    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}
