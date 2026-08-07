using AzureBuddy.Core.Auth;
using AzureBuddy.Core.Common;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AzureBuddy.Core.Account;

/// <summary>
/// Backs the self-service "my account" screen: viewing/updating your own display name and email, and
/// changing your own password. Every method here takes an already-authenticated userId (from the
/// caller's own JWT, via ClaimsPrincipalExtensions.GetRequiredUserId) and only ever reads/writes that
/// one user's row - there is no path here that can touch another account.
/// </summary>
public sealed class AccountService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _dbContext;

    public AccountService(UserManager<ApplicationUser> userManager, AppDbContext dbContext)
    {
        _userManager = userManager;
        _dbContext = dbContext;
    }

    public async Task<ProfileView> GetProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId);
        var roles = await _userManager.GetRolesAsync(user);
        return ToView(user, roles);
    }

    /// <summary>
    /// Updates DisplayName and/or Email. Email changes are checked against every other account first
    /// (Identity's own uniqueness validator would catch it too on save, but checking first gives a
    /// clean, specific "already in use" error instead of a generic Identity failure code) and applied
    /// immediately - there's no email-verification flow yet (IEmailSender is still a no-op stub), so
    /// this is a known, deliberate gap: a production version would send a confirmation link to the NEW
    /// address and only switch over once it's clicked, rather than trusting whatever was typed in.
    ///
    /// RequiresReLogin on the result is true only when Email actually changed. This app's JWTs bake the
    /// email in as a claim at mint time (see TokenService.CreateAccessToken) and nothing re-mints it
    /// mid-session, so a changed email leaves every already-issued token showing the OLD address until
    /// it's replaced. Two ways to handle that: (a) tell the caller to force a re-login, which mints a
    /// fresh token with the new claim immediately - simple, and correct without any extra plumbing; or
    /// (b) reissue a new access/refresh pair as part of this very response so the frontend can swap
    /// them in without interrupting the session. This picks (a): it's a rare action (unlike a role
    /// change, which this app already tolerates lagging until next refresh - see that method's own
    /// docs), and the user just typed their new email into a form, so they're already looking at the
    /// screen and a "please sign in again" redirect is a small, honest cost for immediately showing the
    /// correct address everywhere instead of leaving it stale in the top bar for up to
    /// Jwt:AccessTokenMinutes. (b) would also need to solve the same "which of several active refresh
    /// tokens is THIS session's" problem that ChangePasswordAsync's optional RefreshToken parameter
    /// exists for - solvable, but more moving parts for a change users make rarely.
    /// </summary>
    public async Task<UpdateProfileResult> UpdateProfileAsync(string userId, UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId);
        var emailChanging = !string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase);

        if (emailChanging)
        {
            var existing = await _userManager.FindByEmailAsync(request.Email);
            if (existing is not null && existing.Id != user.Id)
            {
                return UpdateProfileResult.Fail(new ApiError(
                    "duplicate_email", "That email address is already in use by another account.", "email"));
            }
        }

        user.DisplayName = request.DisplayName;

        if (emailChanging)
        {
            // SetEmailAsync/SetUserNameAsync (not a raw `user.Email = ...` + UpdateAsync) are what
            // actually update Identity's NormalizedEmail/NormalizedUserName columns too - skipping
            // that would leave FindByEmailAsync/login silently matching against the OLD address forever,
            // since Identity always looks up by the normalized column, never the raw one. Both persist
            // immediately (including the DisplayName change just set above, since it's the same
            // in-memory user object), and SetEmailAsync also resets EmailConfirmed to false
            // automatically when the address actually changes - the honest state here, since nothing
            // has confirmed the new address exists.
            var setEmailResult = await _userManager.SetEmailAsync(user, request.Email);
            if (!setEmailResult.Succeeded)
            {
                return UpdateProfileResult.Fail(ToApiErrors(setEmailResult.Errors));
            }

            var setUserNameResult = await _userManager.SetUserNameAsync(user, request.Email);
            if (!setUserNameResult.Succeeded)
            {
                return UpdateProfileResult.Fail(ToApiErrors(setUserNameResult.Errors));
            }
        }
        else
        {
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                return UpdateProfileResult.Fail(ToApiErrors(updateResult.Errors));
            }
        }

        var roles = await _userManager.GetRolesAsync(user);
        return UpdateProfileResult.Ok(ToView(user, roles), requiresReLogin: emailChanging);
    }

    /// <summary>
    /// UserManager.ChangePasswordAsync is Identity's own built-in flow: it verifies currentPassword
    /// against the stored hash through the same PasswordHasher (constant-time comparison) LoginAsync
    /// already relies on, applies the configured password policy to newPassword, bumps the user's
    /// security stamp, and re-hashes/persists - all through code that's already been reviewed and
    /// hardened as part of ASP.NET Core Identity itself. Hand-rolling "look up the user, compare the
    /// old password, write the new one" here would mean re-implementing that comparison ourselves,
    /// which is exactly the kind of place a subtle bug (a non-constant-time compare, skipping the
    /// policy check, forgetting the security stamp) creeps in silently - there is no good reason to
    /// take that risk when the built-in method already does it correctly.
    /// </summary>
    public async Task<ChangePasswordResult> ChangePasswordAsync(string userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId);

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            // passwordField: "newPassword" here (not the default "password") - unlike register/reset,
            // this form has two password fields, and every generic policy failure Identity reports
            // (too short, needs a digit, ...) is about newPassword specifically. PasswordMismatch (wrong
            // current password) is mapped to "currentPassword" by IdentityErrorMapping regardless of
            // this parameter - it can only ever mean that field.
            return ChangePasswordResult.Fail(result.Errors
                .Select(e => new ApiError(e.Code, e.Description, IdentityErrorMapping.FieldFor(e.Code, passwordField: "newPassword")))
                .ToArray());
        }

        await RevokeOtherSessionsAsync(userId, request.RefreshToken, cancellationToken);

        return ChangePasswordResult.Ok();
    }

    /// <summary>
    /// Deliberate security practice, not an incidental side effect: a password change is exactly the
    /// moment a user is most likely acting on a suspected compromise (they believe - or know - someone
    /// else has their password), so this is exactly when every OTHER active session should be forced to
    /// re-authenticate. Without this, an attacker who already stole a valid refresh token before the
    /// change would keep their session alive indefinitely even after the legitimate user "fixed"
    /// things - defeating the entire point of changing the password. The refresh token behind THIS
    /// request, if the caller supplied one, is deliberately spared: the tab the user just used to
    /// change their own password should not itself be logged out by the very action of changing it.
    /// </summary>
    private async Task RevokeOtherSessionsAsync(string userId, string? currentRefreshToken, CancellationToken cancellationToken)
    {
        var keepHash = currentRefreshToken is null ? null : TokenService.HashToken(currentRefreshToken);

        var activeTokens = await _dbContext.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (activeTokens.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var revokedAny = false;
        foreach (var token in activeTokens)
        {
            if (keepHash is not null && token.TokenHash == keepHash)
            {
                continue;
            }
            token.RevokedAt = now;
            revokedAny = true;
        }

        if (revokedAny)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<ApplicationUser> RequireUserAsync(string userId)
    {
        // Unreachable in practice behind [Authorize]: userId comes straight from a verified JWT's own
        // "sub" claim, which only ever exists because AuthService issued it for a real user row.
        return await _userManager.FindByIdAsync(userId)
            ?? throw new InvalidOperationException($"Authenticated request referenced a user id ({userId}) with no matching account.");
    }

    private static ProfileView ToView(ApplicationUser user, IList<string> roles) =>
        new(user.Email ?? string.Empty, user.DisplayName, roles.ToArray());

    private static ApiError[] ToApiErrors(IEnumerable<IdentityError> errors) =>
        errors.Select(e => new ApiError(e.Code, e.Description, IdentityErrorMapping.FieldFor(e.Code))).ToArray();
}
