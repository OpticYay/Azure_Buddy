using Microsoft.AspNetCore.Identity;

namespace AzureBuddy.Data.Entities;

/// <summary>
/// Extends Identity's built-in IdentityUser (which already gives us Email, PasswordHash, security
/// stamp, lockout fields, etc.) with the couple of extra fields our app actually needs. We use the
/// default `string` (GUID) primary key rather than a custom key type - simpler, and there's no
/// performance requirement here that would justify the extra complexity of an int/long key.
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
