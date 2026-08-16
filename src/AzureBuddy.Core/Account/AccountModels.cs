using System.ComponentModel.DataAnnotations;
using AzureBuddy.Core.Common;

namespace AzureBuddy.Core.Account;

public sealed record ProfileView(string Email, string DisplayName, IReadOnlyList<string> Roles);

public sealed record UpdateProfileRequest(
    [Required] string DisplayName,
    [Required, EmailAddress] string Email);

/// <summary>
/// RequiresReLogin is true only when Email actually changed - DisplayName isn't baked into the JWT
/// (see TokenService.CreateAccessToken, which only claims sub/email/role), so a display-name-only
/// update never needs one. See AccountService.UpdateProfileAsync's docs for why email changes force a
/// re-login (approach (a)) instead of silently reissuing tokens (approach (b)).
/// </summary>
public sealed record UpdateProfileResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ApiError> Errors { get; init; } = Array.Empty<ApiError>();
    public ProfileView? Profile { get; init; }
    public bool RequiresReLogin { get; init; }

    public static UpdateProfileResult Ok(ProfileView profile, bool requiresReLogin) =>
        new() { Success = true, Profile = profile, RequiresReLogin = requiresReLogin };

    public static UpdateProfileResult Fail(params ApiError[] errors) => new() { Success = false, Errors = errors };
}

/// <summary>
/// RefreshToken is optional and NOT validated as belonging to this user before use - it only ever
/// narrows which of the caller's own refresh tokens gets spared from the post-password-change
/// revocation (see AccountService.ChangePasswordAsync). Omitting it (or passing anything that doesn't
/// match a stored token) just means every session gets revoked, which is still safe, only more
/// aggressive - there's no way to use this field to affect anyone else's tokens.
/// </summary>
public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required] string NewPassword,
    string? RefreshToken);

public sealed record ChangePasswordResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ApiError> Errors { get; init; } = Array.Empty<ApiError>();

    public static ChangePasswordResult Ok() => new() { Success = true };
    public static ChangePasswordResult Fail(params ApiError[] errors) => new() { Success = false, Errors = errors };
}
