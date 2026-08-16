namespace AzureBuddy.Core.Auth;

/// <summary>Bound from the "Email:Smtp" config section (see appsettings.json). Left with an empty
/// Host by default - AuthServiceCollectionExtensions checks Host to decide whether to register
/// SmtpEmailSender or fall back to NoOpEmailSender, so simply not filling this section in keeps the
/// app running exactly as it did before this feature existed.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = "Azure Buddy";
}
