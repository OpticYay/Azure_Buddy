using System.ComponentModel.DataAnnotations;
using AzureBuddy.Core.Common;

namespace AzureBuddy.Core.Auth;

// [ApiController] automatically returns a 400 with the validation errors if these attributes fail -
// no manual "if (string.IsNullOrEmpty(...))" checks needed in AuthController. Password intentionally
// only gets [Required] here, not a hardcoded [MinLength]/[RegularExpression] - actual complexity is
// enforced by Identity's configurable PasswordOptions (see Program.cs's Identity: section), which was
// deliberately made config-driven rather than hardcoded; duplicating a second, code-fixed rule here
// would work against that.
//
// Attributes target the PARAMETER, not "[property: ...]" - for a record's primary constructor,
// ASP.NET Core 8's validation pipeline specifically requires that placement and throws
// InvalidOperationException at request time if attributes are attached to the generated property
// instead ("validation metadata... must be associated with the constructor parameter"). This was
// caught by an integration test (SmokeTests.Register_ReturnsTokens) hitting the real HTTP pipeline -
// a plain unit test of this DTO wouldn't have exercised ASP.NET Core's model-validation code path.

public sealed record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    [Required] string DisplayName);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record RefreshRequest([Required] string RefreshToken);
public sealed record LogoutRequest([Required] string RefreshToken);

public sealed record AuthTokens(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken);

public sealed record AuthResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ApiError> Errors { get; init; } = Array.Empty<ApiError>();
    public AuthTokens? Tokens { get; init; }

    public static AuthResult Ok(AuthTokens tokens) => new() { Success = true, Tokens = tokens };
    public static AuthResult Fail(params ApiError[] errors) => new() { Success = false, Errors = errors };
}
