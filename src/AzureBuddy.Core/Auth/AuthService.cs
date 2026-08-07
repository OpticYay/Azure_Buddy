using System.Text;
using AzureBuddy.Core.Common;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IEmailSender _emailSender;
    private readonly AppOptions _appOptions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        AppDbContext dbContext,
        TokenService tokenService,
        IEmailSender emailSender,
        IOptions<AppOptions> appOptions,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _tokenService = tokenService;
        _emailSender = emailSender;
        _appOptions = appOptions.Value;
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

        // Best-effort and non-blocking: registration already succeeded (the account exists, the caller
        // is about to get tokens and be logged in immediately - this app doesn't gate login on a
        // confirmed email, see ConfirmEmailAsync's docs for why). A misconfigured/unreachable SMTP
        // server should never turn a successful registration into a failed HTTP response.
        try
        {
            await SendConfirmationEmailAsync(user, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send confirmation email to {Email} after registration.", request.Email);
        }

        return AuthResult.Ok(await IssueTokensAsync(user, cancellationToken));
    }

    /// <summary>
    /// Always reports success regardless of whether the email has an account - same non-enumeration
    /// principle as LoginAsync's invalid_credentials error (see there). If an account exists, emails a
    /// reset link built from Identity's own GeneratePasswordResetTokenAsync; if not, this is a no-op
    /// that still looks identical to the caller. A send failure (bad SMTP config, provider outage)
    /// must ALSO look identical to the caller - letting it propagate as an unhandled exception would
    /// turn "account exists but the email failed" into a 500 that's trivially distinguishable from the
    /// 204 an unknown email gets, defeating the whole point of this method's shape. Logged instead, the
    /// same way RegisterAsync treats its own confirmation-email send as best-effort.
    /// </summary>
    public async Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user is null)
        {
            return;
        }

        try
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var link = BuildLink("reset-password", email, token);

            await _emailSender.SendAsync(
                email,
                "Reset your Azure Buddy password",
                $"Someone (hopefully you) requested a password reset for your Azure Buddy account.\n\n" +
                $"Reset your password: {link}\n\n" +
                $"If you didn't request this, you can safely ignore this email - your password won't change.",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send password reset email to {Email}.", email);
        }
    }

    public async Task<AuthResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var invalidToken = new ApiError("invalid_token", "This reset link is invalid or has expired. Request a new one.");

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Same shape as an expired/tampered token - an attacker probing this endpoint learns
            // nothing about whether the email has an account either way.
            return AuthResult.Fail(invalidToken);
        }

        string decodedToken;
        try
        {
            decodedToken = DecodeToken(request.Token);
        }
        catch (FormatException)
        {
            return AuthResult.Fail(invalidToken);
        }

        var result = await _userManager.ResetPasswordAsync(user, decodedToken, request.NewPassword);
        if (!result.Succeeded)
        {
            // Identity reports an expired/already-used/tampered token as "InvalidToken" - collapse that
            // (and only that) into the same generic message as above; genuine password-policy failures
            // (too short, etc.) still get their own specific, field-targeted error.
            var errors = result.Errors
                .Select(e => e.Code == "InvalidToken"
                    ? invalidToken
                    : new ApiError(e.Code, e.Description, IdentityFieldFor(e.Code)))
                .ToArray();
            return AuthResult.Fail(errors);
        }

        return AuthResult.Ok(await IssueTokensAsync(user, cancellationToken));
    }

    public async Task<AuthResult> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken = default)
    {
        var invalidToken = new ApiError("invalid_token", "This confirmation link is invalid or has expired.");

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return AuthResult.Fail(invalidToken);
        }

        string decodedToken;
        try
        {
            decodedToken = DecodeToken(request.Token);
        }
        catch (FormatException)
        {
            return AuthResult.Fail(invalidToken);
        }

        var result = await _userManager.ConfirmEmailAsync(user, decodedToken);
        return result.Succeeded ? AuthResult.Ok(await IssueTokensAsync(user, cancellationToken)) : AuthResult.Fail(invalidToken);
    }

    /// <summary>Same non-enumeration shape as ForgotPasswordAsync - always looks like success, and a
    /// send failure is swallowed (logged) for the same reason: it must not become a 500 that's
    /// distinguishable from the 204 an unknown/already-confirmed email gets.</summary>
    public async Task ResendConfirmationEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user is null || await _userManager.IsEmailConfirmedAsync(user))
        {
            return;
        }

        try
        {
            await SendConfirmationEmailAsync(user, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resend confirmation email to {Email}.", email);
        }
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

    /// <summary>Thin alias kept so call sites here read the same as before - see IdentityErrorMapping
    /// for the actual mapping, now shared with AccountService's change-password endpoint.</summary>
    private static string? IdentityFieldFor(string identityErrorCode) => IdentityErrorMapping.FieldFor(identityErrorCode);

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

    private async Task SendConfirmationEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = BuildLink("confirm-email", user.Email!, token);

        await _emailSender.SendAsync(
            user.Email!,
            "Confirm your Azure Buddy email",
            $"Welcome to Azure Buddy! Confirm your email address to finish setting up your account:\n\n{link}",
            cancellationToken);
    }

    private string BuildLink(string frontendPath, string email, string identityToken) =>
        $"{_appOptions.FrontendBaseUrl.TrimEnd('/')}/{frontendPath}" +
        $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(EncodeToken(identityToken))}";

    // Identity's reset/confirmation tokens are arbitrary bytes that, once run through
    // DataProtector, often contain '+', '/', or '=' - all of which need escaping in a URL query
    // string. Round-tripping through URL-safe base64 (the same '+'->'-', '/'->'_', no-padding
    // convention JWTs use) avoids the token itself getting mangled by different URL encoders in the
    // link (this string) vs. the query-string parser that reads it back out client-side.
    private static string EncodeToken(string token) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(token)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeToken(string encoded)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
