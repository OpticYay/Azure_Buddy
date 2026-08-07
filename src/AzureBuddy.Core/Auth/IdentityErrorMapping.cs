namespace AzureBuddy.Core.Auth;

/// <summary>
/// Identity's built-in error codes are a fixed, known set (see IdentityErrorDescriber) - this maps
/// each one to the request field a frontend should attach it to, so a form can show "too short" next
/// to the password box instead of in a generic banner. Shared between AuthService (register/reset-
/// password, which each have exactly one password field) and AccountService (change-password, which
/// has two - current and new).
/// </summary>
public static class IdentityErrorMapping
{
    /// <param name="identityErrorCode">e.g. "PasswordTooShort", "DuplicateEmail", "PasswordMismatch".</param>
    /// <param name="passwordField">Which field name a generic password-policy failure (too short, needs
    /// a digit, etc.) should point at. Defaults to "password" for the single-password-field forms;
    /// AccountService.ChangePasswordAsync passes "newPassword" since that's the field the policy
    /// actually applies to there. "PasswordMismatch" (wrong current password) always maps to
    /// "currentPassword" regardless of this parameter - it can only ever mean that one field.</param>
    public static string? FieldFor(string identityErrorCode, string passwordField = "password") => identityErrorCode switch
    {
        "PasswordMismatch" => "currentPassword",
        _ when identityErrorCode.StartsWith("Password", StringComparison.Ordinal) => passwordField,
        "DuplicateEmail" or "InvalidEmail" or "DuplicateUserName" or "InvalidUserName" => "email",
        _ => null,
    };
}
