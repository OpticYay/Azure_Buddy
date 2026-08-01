namespace AzureBuddy.Core.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);

public sealed record AuthTokens(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken);

public sealed record AuthResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public AuthTokens? Tokens { get; init; }

    public static AuthResult Ok(AuthTokens tokens) => new() { Success = true, Tokens = tokens };
    public static AuthResult Fail(params string[] errors) => new() { Success = false, Errors = errors };
}
