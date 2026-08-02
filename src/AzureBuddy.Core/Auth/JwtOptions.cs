namespace AzureBuddy.Core.Auth;

/// <summary>Binds to the "Jwt" config section. SigningKey must be a long, random secret (256+ bits) -
/// treat it like a password: never commit a real value, load it from user-secrets/environment/Key Vault.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; init; } = string.Empty;
    public string Issuer { get; init; } = "AzureBuddy";
    public string Audience { get; init; } = "AzureBuddy";
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
}
