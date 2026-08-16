namespace AzureBuddy.Core.Common;

/// <summary>Bound from the "App" config section. Currently just the frontend's own base URL, needed
/// so backend-generated links (password reset, email confirmation) point at wherever the Angular app
/// is actually served rather than assuming localhost.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";
}
