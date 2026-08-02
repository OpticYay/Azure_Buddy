using AzureBuddy.Core.Common;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Auth;

/// <summary>
/// Orchestrates register/login/refresh/logout on top of Identity's UserManager (for password
/// hashing/verification/lockout) and TokenService (for JWT + refresh token generation). Kept out of
/// the controller so the actual auth logic is testable and the controller stays a thin HTTP adapter.
/// </summary>
public sealed class AuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _dbContext;
    private readonly TokenService _tokenService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AppDbContext dbContext,
        TokenService tokenService,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName,
            CreatedAt = DateTime.UtcNow
        };

        // CreateAsync runs the password through Identity's configured policy (min length, complexity,
        // etc. - see Program.cs) and hashes it with PBKDF2 before anything touches the database. We
        // never see or log the plaintext password ourselves.
        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            // Identity's own IdentityError already carries a stable Code (e.g. "PasswordTooShort",
            // "DuplicateUserName") - previously only .Description (the human text) made it into the
            // response, discarding exactly the machine-readable part a frontend would want to switch
            // on. IdentityFieldFor maps each code to which form field it's actually about.
            return AuthResult.Fail(createResult.Errors
                .Select(e => new ApiError(e.Code, e.Description, IdentityFieldFor(e.Code)))
                .ToArray());
        }

        return AuthResult.Ok(await IssueTokensAsync(user, cancellationToken));
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        // Deliberately identical failure message (and code) whether the email doesn't exist or the
        // password is wrong - distinguishing the two lets an attacker enumerate which emails have
        // accounts. No Field either, for the same reason: pointing at "email" vs "password" specifically
        // would itself leak which one was wrong.
        var invalidCredentials = new ApiError("invalid_credentials", "Invalid email or password.");

        if (user is null)
        {
            return AuthResult.Fail(invalidCredentials);
        }

        // CheckPasswordAsync (via PasswordHasher) does a constant-time comparison of the hash, and
        // UserManager tracks failed attempts toward the lockout policy configured in Program.cs.
        var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            _logger.LogWarning("Failed login attempt for {Email}.", request.Email);
            return AuthResult.Fail(invalidCredentials);
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            return AuthResult.Fail(new ApiError(
                "account_locked",
                "Account is temporarily locked due to repeated failed login attempts. Try again later."));
        }

        return AuthResult.Ok(await IssueTokensAsync(user, cancellationToken));
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = TokenService.HashToken(refreshToken);

        var stored = await _dbContext.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        // IsActive checks RevokedAt is null AND ExpiresAt is in the future - a reused, revoked, or
        // expired refresh token is rejected the same way as one that was never issued.
        if (stored is null || !stored.IsActive || stored.User is null)
        {
            return AuthResult.Fail(new ApiError("invalid_refresh_token", "Invalid or expired refresh token."));
        }

        // Rotate on every use: the presented token is revoked and a brand new one issued. If a leaked
        // refresh token is ever replayed by an attacker after the legitimate user has already used it,
        // it will already be revoked and the replay fails - this limits the damage window of a leak.
        stored.RevokedAt = DateTime.UtcNow;

        var tokens = await IssueTokensAsync(stored.User, cancellationToken);
        return AuthResult.Ok(tokens);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = TokenService.HashToken(refreshToken);

        var stored = await _dbContext.RefreshTokens
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Identity's built-in error codes are a fixed, known set (see IdentityErrorDescriber) -
    /// this just groups the ones about the password vs. the ones about the email/username into which
    /// form field a frontend should attach the error to. Falls back to null (a general, non-field-specific
    /// error) for anything else - not the caller's job to guess at fields Identity didn't clearly imply.</summary>
    private static string? IdentityFieldFor(string identityErrorCode) => identityErrorCode switch
    {
        _ when identityErrorCode.StartsWith("Password", StringComparison.Ordinal) => "password",
        "DuplicateEmail" or "InvalidEmail" or "DuplicateUserName" or "InvalidUserName" => "email",
        _ => null,
    };

    private async Task<AuthTokens> IssueTokensAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        // Read fresh from the database on every token issuance (login, register, and refresh all
        // route through here) rather than caching roles anywhere - this is the one point where a
        // just-promoted admin's NEXT token actually reflects it.
        var roles = await _userManager.GetRolesAsync(user);
        var accessToken = _tokenService.CreateAccessToken(user, roles);
        var refreshToken = _tokenService.GenerateRefreshToken();

        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.HashToken(refreshToken),
            ExpiresAt = _tokenService.RefreshTokenExpiry()
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokens(accessToken.Token, accessToken.ExpiresAtUtc, refreshToken);
    }
}
