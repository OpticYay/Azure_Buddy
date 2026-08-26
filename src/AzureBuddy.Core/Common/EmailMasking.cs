namespace AzureBuddy.Core.Common;

/// <summary>Masks an email address for logging - "j***@example.com" rather than the full address.
/// Used anywhere a log line needs to identify WHICH account something happened to (a failed login, an
/// unmatched Admin:Emails entry) without persisting the full address in plaintext in log storage,
/// which is typically retained far longer and read by a wider audience than the application's own
/// user data store.</summary>
public static class EmailMasking
{
    public static string Mask(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return string.Empty;
        }

        var atIndex = email.IndexOf('@');
        if (atIndex <= 0)
        {
            // No '@', or it's the very first character - not a well-formed address to begin with, so
            // there's no meaningful local-part/domain split to mask around. Mask the whole thing rather
            // than risk echoing an unexpected value back verbatim.
            return "***";
        }

        return $"{email[0]}***{email[atIndex..]}";
    }
}
