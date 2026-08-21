using AzureBuddy.Core.Llm;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace AzureBuddy.Tests.Llm;

public class LlmApiKeyProtectorTests
{
    private static LlmApiKeyProtector NewProtector() =>
        new(DataProtectionProvider.Create("AzureBuddy.Tests.LlmApiKeyProtector"));

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsToTheOriginalValue()
    {
        var protector = NewProtector();
        const string plaintext = "super-secret-gemini-api-key";

        var encrypted = protector.Encrypt(plaintext);
        var decrypted = protector.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_GarbageCiphertext_ThrowsCryptographicException()
    {
        var protector = NewProtector();

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protector.Decrypt("garbage"));
    }

    [Fact]
    public void Decrypt_CiphertextFromPatProtector_ThrowsRatherThanReturningWrongData()
    {
        // LlmApiKeyProtector and PatProtector use different purpose strings on the same underlying
        // Data Protection provider precisely so a Gemini key and a user's ADO PAT can never be
        // decrypted with each other's protector even if a ciphertext ended up in the wrong column.
        var provider = DataProtectionProvider.Create("AzureBuddy.Tests.CrossProtector");
        var patProtector = new AzureBuddy.Core.Settings.PatProtector(provider);
        var llmKeyProtector = new LlmApiKeyProtector(provider);

        var encryptedAsPat = patProtector.Encrypt("some-value");

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => llmKeyProtector.Decrypt(encryptedAsPat));
    }
}
