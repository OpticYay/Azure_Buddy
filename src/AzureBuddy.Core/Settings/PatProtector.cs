using Microsoft.AspNetCore.DataProtection;

namespace AzureBuddy.Core.Settings;

/// <summary>
/// Thin wrapper around ASP.NET Core's Data Protection API, scoped to one specific "purpose" string
/// (see CreateProtector below). Encryption here is symmetric and reversible - unlike a password hash,
/// we genuinely need the original PAT back later to call Azure DevOps, so hashing (one-way) isn't an
/// option; Data Protection gives us authenticated encryption (AES + HMAC under the hood) instead.
///
/// The actual encryption key is managed by Data Protection itself, not by this class - see
/// Program.cs's PersistKeysToFileSystem/SetApplicationName call for where that key material lives.
/// Losing that key means every already-encrypted PAT in the database becomes permanently unreadable,
/// which is why the key ring's storage location matters (documented in the README).
/// </summary>
public sealed class PatProtector
{
    // The purpose string is mixed into key derivation - changing it would make previously-encrypted
    // PATs unreadable (Data Protection deliberately isolates protectors created with different purposes).
    private const string Purpose = "AzureBuddy.UserAdoSettings.Pat.v1";

    private readonly IDataProtector _protector;

    public PatProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Encrypt(string plaintextPat) => _protector.Protect(plaintextPat);

    /// <summary>Only ever call this in memory, immediately before making an ADO API call. The
    /// decrypted result must never be logged, returned in an API response, or persisted anywhere.</summary>
    public string Decrypt(string encryptedPat) => _protector.Unprotect(encryptedPat);
}
