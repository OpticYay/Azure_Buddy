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
            return AuthResult.Fail(createResult.Errors.Select(e => e.Description).ToArray());
        }

        return AuthResult.Ok(await IssueTokensAsync(user, cancellationToken));
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        // Deliberately identical failure message whether the email doesn't exist or the password is
        // wrong - distinguishing the two lets an attacker enumerate which emails have accounts.
        const string genericFailure = "Invalid email or password.";

        if (user is null)
        {
            return AuthResult.Fail(genericFailure);
        }

        // CheckPasswordAsync (via PasswordHasher) does a constant-time comparison of the hash, and
        // UserManager tracks failed attempts toward the lockout policy configured in Program.cs.
        var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            _logger.LogWarning("Failed login attempt for {Email}.", request.Email);
            return AuthResult.Fail(genericFailure);
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            return AuthResult.Fail("Account is temporarily locked due to repeated failed login attempts. Try again later.");
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
            return AuthResult.Fail("Invalid or expired refresh token.");
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

    private async Task<AuthTokens> IssueTokensAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var accessToken = _tokenService.CreateAccessToken(user);
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
