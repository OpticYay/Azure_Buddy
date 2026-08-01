namespace AzureBuddy.Data.Entities;

/// <summary>
/// One row per user holding their personal Azure DevOps connection details. This replaces the old
/// global ADO_ORG/ADO_PROJECT/PAT config - every ADO call the app makes must now be scoped to
/// whichever user is making the request.
///
/// EncryptedPat is exactly that: ciphertext, never the raw token. It's produced by ASP.NET Core's
/// Data Protection API (see Settings/PatProtector.cs in AzureBuddy.Core) before it ever reaches this
/// table, and is only decrypted in memory, just before an outbound call to Azure DevOps.
/// </summary>
public sealed class UserAdoSettings
{
    public int Id { get; set; }

    public required string UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public required string OrganizationUrl { get; set; }
    public required string DefaultProject { get; set; }
    public required string EncryptedPat { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
