using AzureBuddy.Core.Common;
using Microsoft.AspNetCore.DataProtection;

namespace AzureBuddy.Core.Llm;

/// <summary>Encrypts/decrypts the admin-configured Gemini API key, at rest in
/// LlmSettings.GeminiEncryptedApiKey. See <see cref="NamedSecretProtector"/> for the shared
/// implementation and why this stays its own named type rather than a bare instance of the base
/// class.</summary>
public sealed class LlmApiKeyProtector : NamedSecretProtector
{
    private const string Purpose = "AzureBuddy.LlmSettings.GeminiApiKey.v1";

    public LlmApiKeyProtector(IDataProtectionProvider provider) : base(provider, Purpose)
    {
    }
}
