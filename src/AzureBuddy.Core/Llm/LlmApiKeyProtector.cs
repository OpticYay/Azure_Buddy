using Microsoft.AspNetCore.DataProtection;

namespace AzureBuddy.Core.Llm;

/// <summary>
/// Encrypts/decrypts the admin-configured Gemini API key, at rest in LlmSettings.GeminiEncryptedApiKey.
/// Deliberately its own class with its own purpose string, mirroring AzureBuddy.Core.Settings.PatProtector
/// exactly (same Data Protection API, same "encrypt reversibly, never hash" reasoning - we need the
/// real key back to call Gemini, so a one-way hash isn't an option here either) rather than reusing
/// PatProtector directly: Data Protection deliberately isolates protectors created with different
/// purpose strings, so an admin's Gemini key and a user's ADO PAT can never be decrypted with each
/// other's protector even if both ciphertexts somehow ended up in the wrong column.
/// </summary>
public sealed class LlmApiKeyProtector
{
    private const string Purpose = "AzureBuddy.LlmSettings.GeminiApiKey.v1";

    private readonly IDataProtector _protector;

    public LlmApiKeyProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Encrypt(string plaintextApiKey) => _protector.Protect(plaintextApiKey);

    /// <summary>Only ever call this in memory, immediately before making an outbound Gemini call. The
    /// decrypted result must never be logged, returned in an API response, or persisted anywhere.</summary>
    public string Decrypt(string encryptedApiKey) => _protector.Unprotect(encryptedApiKey);
}
