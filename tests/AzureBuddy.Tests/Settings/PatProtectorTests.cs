using AzureBuddy.Core.Settings;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace AzureBuddy.Tests.Settings;

public class PatProtectorTests
{
    private static PatProtector NewProtector() =>
        new(DataProtectionProvider.Create("AzureBuddy.Tests.PatProtector"));

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsToTheOriginalValue()
    {
        var protector = NewProtector();
        const string plaintext = "super-secret-pat-value";

        var encrypted = protector.Encrypt(plaintext);
        var decrypted = protector.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesCiphertextDifferentFromThePlaintext()
    {
        var protector = NewProtector();

        var encrypted = protector.Encrypt("a-real-pat");

        Assert.NotEqual("a-real-pat", encrypted);
    }

    [Fact]
    public void Decrypt_GarbageCiphertext_ThrowsCryptographicException()
    {
        var protector = NewProtector();

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protector.Decrypt("not-a-real-protected-payload"));
    }

    [Fact]
    public void Decrypt_CiphertextFromADifferentPurpose_ThrowsRatherThanReturningWrongData()
    {
        // Data Protection deliberately isolates protectors created with different purpose strings - a
        // ciphertext produced under one purpose must never decrypt successfully under another, even
        // from the same key ring, or a bug that mixed up two protectors would silently return garbage
        // instead of failing loudly.
        var provider = DataProtectionProvider.Create("AzureBuddy.Tests.PatProtector.CrossPurpose");
        var patProtector = new PatProtector(provider);
        var otherPurposeProtector = provider.CreateProtector("SomeOtherPurpose.v1");

        var encryptedUnderOtherPurpose = otherPurposeProtector.Protect("a-real-pat");

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => patProtector.Decrypt(encryptedUnderOtherPurpose));
    }
}
