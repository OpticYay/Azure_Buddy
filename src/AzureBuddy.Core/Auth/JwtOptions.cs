using System.ComponentModel.DataAnnotations;

namespace AzureBuddy.Core.Auth;

/// <summary>Binds to the "Jwt" config section. SigningKey must be a long, random secret (256+ bits) -
/// treat it like a password: never commit a real value, load it from user-secrets/environment/Key Vault.
/// The 32-character minimum here is a startup-time guard, not the real strength requirement (32 ASCII
/// bytes = 256 bits, matching the "256+ bits" comment above) - without it, a too-short key doesn't fail
/// until the first token is minted, deep inside the JWT library, which is a much harder failure to
/// diagnose in production than a clear startup error.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required, MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;
    public string Issuer { get; init; } = "AzureBuddy";
    public string Audience { get; init; } = "AzureBuddy";
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
}
