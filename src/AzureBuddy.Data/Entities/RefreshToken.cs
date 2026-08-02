namespace AzureBuddy.Data.Entities;

/// <summary>
/// We store a SHA-256 hash of each refresh token, never the token itself - same reasoning as password
/// hashing: if the database is ever read by someone unauthorized, they get hashes they can't turn back
/// into usable tokens. The raw token is handed to the client once (at login/refresh time) and never
/// persisted anywhere in plaintext. See TokenService.HashToken for where the hash is computed.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public required string TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
