using AzureBuddy.Core.Common;
using Microsoft.AspNetCore.DataProtection;

namespace AzureBuddy.Core.Settings;

/// <summary>Encrypts/decrypts a user's Azure DevOps PAT, at rest in UserAdoSettings.EncryptedPat. See
/// <see cref="NamedSecretProtector"/> for the shared implementation and why this stays its own named
/// type rather than a bare instance of the base class.</summary>
public sealed class PatProtector : NamedSecretProtector
{
    // The purpose string is mixed into key derivation - changing it would make previously-encrypted
    // PATs unreadable (Data Protection deliberately isolates protectors created with different purposes).
    private const string Purpose = "AzureBuddy.UserAdoSettings.Pat.v1";

    public PatProtector(IDataProtectionProvider provider) : base(provider, Purpose)
    {
    }
}
