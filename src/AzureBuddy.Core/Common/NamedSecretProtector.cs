using Microsoft.AspNetCore.DataProtection;

namespace AzureBuddy.Core.Common;

/// <summary>
/// Thin wrapper around ASP.NET Core's Data Protection API, scoped to one specific "purpose" string.
/// Encryption here is symmetric and reversible - unlike a password hash, callers genuinely need the
/// original secret back later (to call an external API), so hashing (one-way) isn't an option; Data
/// Protection gives authenticated encryption (AES + HMAC under the hood) instead.
///
/// Base class for PatProtector and LlmApiKeyProtector, which were structurally identical Data
/// Protection wrappers differing only in the purpose string - both are now thin named instances of
/// this so each keeps its own DI-friendly type and its own purpose string (Data Protection
/// deliberately isolates protectors created with different purposes, so a user's ADO PAT and an
/// admin's Gemini key can never be decrypted with each other's protector even if both ciphertexts
/// somehow ended up in the wrong column).
///
/// The actual encryption key is managed by Data Protection itself, not by this class - see
/// Program.cs's PersistKeysToFileSystem/SetApplicationName call for where that key material lives.
/// </summary>
public abstract class NamedSecretProtector
{
    private readonly IDataProtector _protector;

    protected NamedSecretProtector(IDataProtectionProvider provider, string purpose)
    {
        _protector = provider.CreateProtector(purpose);
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    /// <summary>Only ever call this in memory, immediately before making the outbound API call the
    /// secret is for. The decrypted result must never be logged, returned in an API response, or
    /// persisted anywhere.</summary>
    public string Decrypt(string encrypted) => _protector.Unprotect(encrypted);
}
