namespace AzureBuddy.Core.Common;

/// <summary>Shared "••••••••1234"-style credential masking (last 4 characters visible, the rest
/// replaced with bullets, same pattern as a masked credit card) - previously duplicated between
/// LlmSettingsService.MaskKey and UserAdoConfigService.MaskPat.</summary>
public static class SecretMasking
{
    public static string Mask(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var lastFour = plaintext.Length >= 4 ? plaintext[^4..] : plaintext;
        return new string('•', 8) + lastFour;
    }
}
